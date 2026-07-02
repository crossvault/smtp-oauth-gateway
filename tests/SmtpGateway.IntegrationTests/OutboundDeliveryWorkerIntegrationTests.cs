using MimeKit;
using SmtpGateway.Core;
using SmtpGateway.Infrastructure;
using Xunit;

namespace SmtpGateway.IntegrationTests;

/// <summary>
/// End-to-end: a real <see cref="FileSpool"/> + <see cref="SqliteQueueRepository"/> queue item is
/// processed by <see cref="OutboundDeliveryWorker"/> against a real <see cref="GenericSmtpProvider"/>
/// talking to a fake SMTP server over a loopback TCP socket, asserting the item ends up Sent.
/// </summary>
public sealed class OutboundDeliveryWorkerIntegrationTests : IDisposable
{
    private readonly string _root;
    private readonly string _spoolDirectory;
    private readonly string _databasePath;

    public OutboundDeliveryWorkerIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "SmtpGateway.OutboundWorkerIntegrationTests", Guid.NewGuid().ToString("N"));
        _spoolDirectory = Path.Combine(_root, "spool");
        _databasePath = Path.Combine(_root, "queue.db");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // Clear only this test's own connection pool (scoped by connection string) rather than
        // the process-global ClearAllPools(), which would race with other test classes
        // concurrently opening/closing pooled connections for their own unrelated databases.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _databasePath }.ConnectionString))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessNextAsync_RealGenericSmtpProvider_DeliversAndMarksItemSent()
    {
        var ct = TestContext.Current.CancellationToken;

        var spool = new FileSpool(_spoolDirectory);
        var repository = new SqliteQueueRepository(_databasePath);

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        message.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
        message.Subject = "Outbound delivery worker integration test";
        message.Body = new TextPart("plain") { Text = "Hello from the outbound delivery worker integration test." };

        using var rawStream = new MemoryStream();
        await message.WriteToAsync(rawStream, ct);
        var rawMime = rawStream.ToArray();

        var key = Guid.NewGuid();
        var writeResult = await spool.WriteAsync(key, rawMime, ct);

        var envelope = new Envelope("sender@example.com", ["recipient@example.com"]);
        var now = DateTimeOffset.UtcNow;
        var item = new QueueItem
        {
            Id = key,
            Envelope = envelope,
            Recipients = [new RecipientDelivery("recipient@example.com")],
            MimePath = writeResult.Path,
            Hash = writeResult.Hash,
            SizeBytes = writeResult.SizeBytes,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Status = QueueItemStatus.Queued,
        };
        await repository.EnqueueAsync(item, ct);

        using var fakeServer = new FakeSmtpServer();
        var provider = new GenericSmtpProvider(new GenericSmtpProviderOptions
        {
            Host = "127.0.0.1",
            Port = fakeServer.Port,
            TlsMode = SmtpTlsMode.None,
            AuthMode = AuthMode.None,
        });

        var worker = new OutboundDeliveryWorker(repository, spool, provider);

        var processed = await worker.ProcessNextAsync(ct);

        Assert.True(processed);
        var reloaded = await repository.GetByIdAsync(item.Id, ct);
        Assert.Equal(QueueItemStatus.Sent, reloaded!.Status);
        Assert.All(reloaded.Recipients, r => Assert.Equal(RecipientStatus.Sent, r.Status));
    }
}
