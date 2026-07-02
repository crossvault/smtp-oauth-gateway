using System.Buffers;
using System.Text;
using Microsoft.Data.Sqlite;
using SmtpGateway.Core;
using SmtpGateway.Infrastructure;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Protocol;
using Xunit;

namespace SmtpGateway.Infrastructure.Tests;

/// <summary>
/// Exercises the disk-quota backpressure check in <see cref="SpoolingMessageStore.SaveAsync"/>:
/// with no <see cref="GatewayOptions.MaxSpoolBytes"/> configured behavior is unchanged, and once
/// configured an inbound message that would push total spool usage over the limit is rejected
/// with a temporary/retryable SMTP response before anything is written to disk or the queue -
/// never a half-written state (spool file with no queue row, or vice versa).
/// </summary>
public sealed class SpoolingMessageStoreTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root;
    private readonly string _spoolDirectory;
    private readonly string _dbPath;

    public SpoolingMessageStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "SmtpGateway.SpoolingMessageStoreTests", Guid.NewGuid().ToString("N"));
        _spoolDirectory = Path.Combine(_root, "spool");
        _dbPath = Path.Combine(_root, "queue.db");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath }.ConnectionString))
        {
            SqliteConnection.ClearPool(connection);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_NoMaxSpoolBytesConfigured_AlwaysAccepted()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var store = new SpoolingMessageStore(new FileSpool(_spoolDirectory), repository, maxMessageSizeBytes: 10_000_000, maxSpoolBytes: null);

        var response = await SaveAsync(store, "hello, this is the message body"u8.ToArray());

        Assert.Equal(SmtpResponse.Ok.ReplyCode, response.ReplyCode);
        Assert.Single(Directory.GetFiles(_spoolDirectory));
        Assert.Single(await repository.ListAsync(Ct));
    }

    [Fact]
    public async Task SaveAsync_MaxSpoolBytesConfigured_UsagePlusMessageStaysUnderLimit_AcceptedNormally()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var bytes = "hello, this is the message body"u8.ToArray();
        var store = new SpoolingMessageStore(
            new FileSpool(_spoolDirectory), repository, maxMessageSizeBytes: 10_000_000, maxSpoolBytes: bytes.LongLength + 1);

        var response = await SaveAsync(store, bytes);

        Assert.Equal(SmtpResponse.Ok.ReplyCode, response.ReplyCode);
        Assert.Single(Directory.GetFiles(_spoolDirectory));
        Assert.Single(await repository.ListAsync(Ct));
    }

    [Fact]
    public async Task SaveAsync_MaxSpoolBytesConfigured_UsagePlusMessageWouldExceedLimit_RejectedWithTemporaryResponseAndNothingWritten()
    {
        var repository = new SqliteQueueRepository(_dbPath);

        // Pre-existing usage already on disk/in the queue, simulating an earlier accepted
        // message whose spool file is never deleted (Sent items still count toward usage).
        var existing = CreateItem(sizeBytes: 900);
        await repository.EnqueueAsync(existing, Ct);

        var bytes = "hello, this is the message body that pushes usage over quota"u8.ToArray();
        var store = new SpoolingMessageStore(
            new FileSpool(_spoolDirectory), repository, maxMessageSizeBytes: 10_000_000, maxSpoolBytes: 900 + bytes.LongLength - 1);

        var response = await SaveAsync(store, bytes);

        Assert.Equal(SmtpReplyCode.InsufficientStorage, response.ReplyCode);
        // Only the pre-existing item's row exists - the rejected message never got a queue row.
        var items = await repository.ListAsync(Ct);
        Assert.Single(items);
        Assert.Equal(existing.Id, items[0].Id);
        // Nothing was ever written for the rejected message: the spool directory (created empty
        // by the FileSpool constructor) has no files in it.
        Assert.Empty(Directory.GetFiles(_spoolDirectory));
    }

    [Fact]
    public async Task SaveAsync_ConcurrentWritesWithQuota_NeverOvershootTheCap()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var body = "0123456789ABCDEF"u8.ToArray();
        var messageSize = body.LongLength;
        // Cap leaves room for exactly three messages; fire twelve concurrently at it.
        var cap = messageSize * 3;
        var store = new SpoolingMessageStore(
            new FileSpool(_spoolDirectory), repository, maxMessageSizeBytes: 10_000_000, maxSpoolBytes: cap);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 12).Select(_ => Task.Run(() => SaveAsync(store, body))));

        var accepted = responses.Count(r => r.ReplyCode == SmtpResponse.Ok.ReplyCode);
        var rejected = responses.Count(r => r.ReplyCode == SmtpReplyCode.InsufficientStorage);

        // The in-process gate makes the check-then-write-then-enqueue section atomic, so concurrent
        // sessions cannot all read the same committed total, all pass the check, and all commit past
        // the cap. Exactly three fit; the rest are rejected with the temporary 452 response.
        Assert.Equal(3, accepted);
        Assert.Equal(9, rejected);
        Assert.True(await repository.GetTotalSpoolBytesAsync(Ct) <= cap);
    }

    private async Task<SmtpResponse> SaveAsync(SpoolingMessageStore store, byte[] rawMime)
    {
        var transaction = new FakeMessageTransaction
        {
            From = new Mailbox("sender", "example.com"),
            To = { new Mailbox("recipient", "example.com") },
        };

        return await store.SaveAsync(
            context: null!,
            transaction,
            new ReadOnlySequence<byte>(rawMime),
            Ct);
    }

    /// <summary>
    /// The SmtpServer package's own <c>SmtpMessageTransaction</c> implementation is internal to
    /// that assembly, so a minimal stand-in is needed to drive <see cref="SpoolingMessageStore.SaveAsync"/>
    /// directly in a unit test without spinning up a real SMTP session.
    /// </summary>
    private sealed class FakeMessageTransaction : IMessageTransaction
    {
        public IMailbox From { get; set; } = null!;

        public IList<IMailbox> To { get; } = new List<IMailbox>();

        public IReadOnlyDictionary<string, string> Parameters { get; } =
            new Dictionary<string, string>();
    }

    private static QueueItem CreateItem(long sizeBytes)
    {
        var envelope = new Envelope("sender@example.com", new[] { "recipient@example.com" });
        var now = DateTimeOffset.UtcNow;

        return new QueueItem
        {
            Id = Guid.NewGuid(),
            Envelope = envelope,
            Recipients = [new RecipientDelivery("recipient@example.com")],
            MimePath = @"C:\spool\existing.eml",
            Hash = "deadbeef",
            SizeBytes = sizeBytes,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Status = QueueItemStatus.Sent,
        };
    }
}
