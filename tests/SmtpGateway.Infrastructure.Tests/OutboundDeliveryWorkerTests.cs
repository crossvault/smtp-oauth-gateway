using MimeKit;
using SmtpGateway.Core;
using Xunit;

namespace SmtpGateway.Infrastructure.Tests;

public sealed class OutboundDeliveryWorkerTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root;
    private readonly string _spoolDirectory;
    private readonly string _dbPath;

    public OutboundDeliveryWorkerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "SmtpGateway.OutboundDeliveryWorkerTests", Guid.NewGuid().ToString("N"));
        _spoolDirectory = Path.Combine(_root, "spool");
        _dbPath = Path.Combine(_root, "queue.db");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // Clear only this test's own connection pool (scoped by connection string) rather than
        // the process-global ClearAllPools(), which would race with other test classes
        // concurrently opening/closing pooled connections for their own unrelated databases.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _dbPath }.ConnectionString))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task<(SqliteQueueRepository Repository, FileSpool Spool, QueueItem Item)> EnqueueItemAsync(
        string mailFrom, string[] recipients, byte[]? rawMime = null)
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var spool = new FileSpool(_spoolDirectory);
        var key = Guid.NewGuid();
        var bytes = rawMime ?? BuildRawMime(mailFrom, recipients);
        var writeResult = await spool.WriteAsync(key, bytes, Ct);

        var envelope = new Envelope(mailFrom, recipients);
        var now = DateTimeOffset.UtcNow;
        var item = new QueueItem
        {
            Id = key,
            Envelope = envelope,
            Recipients = recipients.Select(r => new RecipientDelivery(r)).ToList(),
            MimePath = writeResult.Path,
            Hash = writeResult.Hash,
            SizeBytes = writeResult.SizeBytes,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Status = QueueItemStatus.Queued,
        };
        await repository.EnqueueAsync(item, Ct);
        return (repository, spool, item);
    }

    private static byte[] BuildRawMime(string mailFrom, IEnumerable<string> recipients)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(mailFrom));
        foreach (var recipient in recipients)
        {
            message.To.Add(MailboxAddress.Parse(recipient));
        }

        message.Subject = "Test";
        message.Body = new TextPart("plain") { Text = "Hello" };

        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return stream.ToArray();
    }

    [Fact]
    public async Task ProcessNextAsync_EmptyQueue_ReturnsFalse()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var spool = new FileSpool(_spoolDirectory);
        var provider = new FakeOutboundProvider(_ => new Dictionary<string, SubmitOutcome>());
        var worker = new OutboundDeliveryWorker(repository, spool, provider);

        var processed = await worker.ProcessNextAsync(Ct);

        Assert.False(processed);
    }

    [Fact]
    public async Task ProcessNextAsync_AllRecipientsSucceed_MarksItemSentWithNoNextAttempt()
    {
        var (repository, spool, item) = await EnqueueItemAsync(
            "sender@example.com", ["a@example.com", "b@example.com"]);
        var provider = new FakeOutboundProvider(envelope => envelope.Recipients
            .ToDictionary(r => r, _ => new SubmitOutcome(OutboundSubmitResult.Success)));
        var worker = new OutboundDeliveryWorker(repository, spool, provider);

        var processed = await worker.ProcessNextAsync(Ct);

        Assert.True(processed);
        var reloaded = await repository.GetByIdAsync(item.Id, Ct);
        Assert.Equal(QueueItemStatus.Sent, reloaded!.Status);
        Assert.Null(reloaded.NextAttemptUtc);
        Assert.All(reloaded.Recipients, r => Assert.Equal(RecipientStatus.Sent, r.Status));
    }

    [Fact]
    public async Task ProcessNextAsync_OneRetryableOnePermanentFailure_SchedulesRetryAndUpdatesStatuses()
    {
        var (repository, spool, item) = await EnqueueItemAsync(
            "sender@example.com", ["retry@example.com", "bad@example.com"]);
        var provider = new FakeOutboundProvider(_ => new Dictionary<string, SubmitOutcome>
        {
            ["retry@example.com"] = new SubmitOutcome(OutboundSubmitResult.RetryableFailure),
            ["bad@example.com"] = new SubmitOutcome(OutboundSubmitResult.PermanentFailure),
        });
        var worker = new OutboundDeliveryWorker(repository, spool, provider);
        var before = DateTimeOffset.UtcNow;

        var processed = await worker.ProcessNextAsync(Ct);

        Assert.True(processed);
        var reloaded = await repository.GetByIdAsync(item.Id, Ct);
        // No recipient was ever Sent, and one is still Retryable -> QueueItemStatusResolver.Derive
        // returns RetryScheduled (PartiallySent requires at least one Sent recipient).
        Assert.Equal(QueueItemStatus.RetryScheduled, reloaded!.Status);
        Assert.Equal(1, reloaded.AttemptCount);
        Assert.NotNull(reloaded.NextAttemptUtc);
        Assert.True(reloaded.NextAttemptUtc >= before + RetryPolicy.GetDelay(1));

        var retryRecipient = reloaded.Recipients.Single(r => r.Address == "retry@example.com");
        Assert.Equal(RecipientStatus.Retryable, retryRecipient.Status);
        Assert.Equal(1, retryRecipient.AttemptCount);

        var badRecipient = reloaded.Recipients.Single(r => r.Address == "bad@example.com");
        Assert.Equal(RecipientStatus.PermanentlyFailed, badRecipient.Status);
    }

    [Fact]
    public async Task ProcessNextAsync_RetryableWithRetryAfterHint_UsesHintInsteadOfStagedDelay()
    {
        var (repository, spool, item) = await EnqueueItemAsync(
            "sender@example.com", ["throttled@example.com"]);
        var retryAfter = TimeSpan.FromMinutes(30);
        var provider = new FakeOutboundProvider(_ => new Dictionary<string, SubmitOutcome>
        {
            ["throttled@example.com"] = new SubmitOutcome(OutboundSubmitResult.RetryableFailure, retryAfter),
        });
        var worker = new OutboundDeliveryWorker(repository, spool, provider);
        var before = DateTimeOffset.UtcNow;

        var processed = await worker.ProcessNextAsync(Ct);

        Assert.True(processed);
        var reloaded = await repository.GetByIdAsync(item.Id, Ct);
        Assert.NotNull(reloaded!.NextAttemptUtc);
        // The provider's Retry-After hint (30 minutes) is far larger than the staged policy's
        // first-attempt delay (1 minute), so honoring the hint must push NextAttemptUtc well past
        // what RetryPolicy.GetDelay(1) alone would produce.
        Assert.True(reloaded.NextAttemptUtc >= before + retryAfter);
        Assert.True(reloaded.NextAttemptUtc < before + retryAfter + TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task ProcessNextAsync_PartialSuccessThenRetry_DeliversRemainingRecipientAndDoesNotResendToAlreadySentOne()
    {
        var (repository, spool, item) = await EnqueueItemAsync(
            "sender@example.com", ["sent@example.com", "retry@example.com"]);
        var submittedRecipients = new List<IReadOnlyList<string>>();
        var callCount = 0;
        var provider = new FakeOutboundProvider(envelope =>
        {
            submittedRecipients.Add(envelope.Recipients);
            callCount++;
            return callCount == 1
                ? new Dictionary<string, SubmitOutcome>
                {
                    ["sent@example.com"] = new SubmitOutcome(OutboundSubmitResult.Success),
                    ["retry@example.com"] = new SubmitOutcome(OutboundSubmitResult.RetryableFailure),
                }
                : envelope.Recipients.ToDictionary(r => r, _ => new SubmitOutcome(OutboundSubmitResult.Success));
        });
        var worker = new OutboundDeliveryWorker(repository, spool, provider);

        var firstProcessed = await worker.ProcessNextAsync(Ct);
        Assert.True(firstProcessed);
        var afterFirst = await repository.GetByIdAsync(item.Id, Ct);
        Assert.Equal(QueueItemStatus.PartiallySent, afterFirst!.Status);

        // Force the scheduled retry to be immediately due.
        await repository.SetNextAttemptAsync(item.Id, afterFirst.AttemptCount, DateTimeOffset.UtcNow.AddMinutes(-1), Ct);

        var secondProcessed = await worker.ProcessNextAsync(Ct);

        Assert.True(secondProcessed);
        Assert.Equal(2, submittedRecipients.Count);
        Assert.Equal(["retry@example.com"], submittedRecipients[1]);

        var reloaded = await repository.GetByIdAsync(item.Id, Ct);
        Assert.Equal(QueueItemStatus.Sent, reloaded!.Status);
        Assert.All(reloaded.Recipients, r => Assert.Equal(RecipientStatus.Sent, r.Status));
    }

    [Fact]
    public async Task ProcessNextAsync_SenderRewriteConfigured_RewritesEnvelopeAndMimeFromButNotStoredData()
    {
        var (repository, spool, item) = await EnqueueItemAsync(
            "original@example.com", ["rcpt@example.com"]);
        Envelope? capturedEnvelope = null;
        byte[]? capturedRawMime = null;
        var provider = new FakeOutboundProvider(envelope =>
        {
            capturedEnvelope = envelope;
            return envelope.Recipients.ToDictionary(r => r, _ => new SubmitOutcome(OutboundSubmitResult.Success));
        }, (envelope, rawMime) =>
        {
            capturedEnvelope = envelope;
            capturedRawMime = rawMime;
        });
        var worker = new OutboundDeliveryWorker(repository, spool, provider, rewriteSenderAddress: "rewritten@example.com");

        await worker.ProcessNextAsync(Ct);

        Assert.NotNull(capturedEnvelope);
        Assert.Equal("rewritten@example.com", capturedEnvelope!.MailFrom);

        Assert.NotNull(capturedRawMime);
        var sentMessage = await MimeMessage.LoadAsync(new MemoryStream(capturedRawMime!), Ct);
        Assert.Equal("rewritten@example.com", sentMessage.From.Mailboxes.Single().Address);

        var reloaded = await repository.GetByIdAsync(item.Id, Ct);
        Assert.Equal("original@example.com", reloaded!.Envelope.MailFrom);

        var originalBytes = await spool.ReadAsync(item.MimePath, Ct);
        var originalMessage = await MimeMessage.LoadAsync(new MemoryStream(originalBytes), Ct);
        Assert.Equal("original@example.com", originalMessage.From.Mailboxes.Single().Address);
    }

    [Fact]
    public async Task ProcessNextAsync_NoRateLimitConfigured_ProcessesItemsBackToBackWithNoDelay()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var spool = new FileSpool(_spoolDirectory);
        for (var i = 0; i < 5; i++)
        {
            await EnqueueOnlyAsync(repository, spool, $"sender{i}@example.com", [$"rcpt{i}@example.com"]);
        }

        var provider = new FakeOutboundProvider(envelope => envelope.Recipients
            .ToDictionary(r => r, _ => new SubmitOutcome(OutboundSubmitResult.Success)));
        // No rateLimiter argument at all - default (unconfigured) behavior must be unchanged.
        var worker = new OutboundDeliveryWorker(repository, spool, provider);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 5; i++)
        {
            Assert.True(await worker.ProcessNextAsync(Ct));
        }
        sw.Stop();

        // Generous upper bound - this only guards against an accidental artificial delay being
        // introduced, not a tight performance assertion.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ProcessNextAsync_RateLimited_FourthAttemptWithinSameMinuteIsDeferredThenAllowedAfterWindowRollsOver()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var spool = new FileSpool(_spoolDirectory);
        var items = new List<QueueItem>();
        for (var i = 0; i < 4; i++)
        {
            items.Add(await EnqueueOnlyAsync(repository, spool, $"sender{i}@example.com", [$"rcpt{i}@example.com"]));
        }

        var submittedCount = 0;
        var provider = new FakeOutboundProvider(envelope =>
        {
            submittedCount++;
            return envelope.Recipients.ToDictionary(r => r, _ => new SubmitOutcome(OutboundSubmitResult.Success));
        });

        var clock = new MutableClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var rateLimiter = new SlidingWindowRateLimiter(maxPerWindow: 3, TimeSpan.FromMinutes(1));
        var worker = new OutboundDeliveryWorker(
            repository, spool, provider, rateLimiter: rateLimiter, utcNowProvider: () => clock.Now);

        // Three submissions within the same instant all succeed - no real sleep involved.
        Assert.True(await worker.ProcessNextAsync(Ct));
        Assert.True(await worker.ProcessNextAsync(Ct));
        Assert.True(await worker.ProcessNextAsync(Ct));
        Assert.Equal(3, submittedCount);

        // The 4th attempt in the same rolling window is deferred: no lease taken, no submission.
        Assert.False(await worker.ProcessNextAsync(Ct));
        Assert.Equal(3, submittedCount);
        var stillQueued = await repository.GetByIdAsync(items[3].Id, Ct);
        Assert.Equal(QueueItemStatus.Queued, stillQueued!.Status);

        // Advance simulated time a full minute - no real delay - so the window rolls over and a
        // slot opens up again.
        clock.Now += TimeSpan.FromMinutes(1);

        Assert.True(await worker.ProcessNextAsync(Ct));
        Assert.Equal(4, submittedCount);
        var finallySent = await repository.GetByIdAsync(items[3].Id, Ct);
        Assert.Equal(QueueItemStatus.Sent, finallySent!.Status);
    }

    [Fact]
    public async Task ProcessNextAsync_RateLimited_PollingEmptyQueueDoesNotConsumeBudgetForLaterMessage()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var spool = new FileSpool(_spoolDirectory);

        var submittedCount = 0;
        var provider = new FakeOutboundProvider(envelope =>
        {
            submittedCount++;
            return envelope.Recipients.ToDictionary(r => r, _ => new SubmitOutcome(OutboundSubmitResult.Success));
        });

        var clock = new MutableClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var rateLimiter = new SlidingWindowRateLimiter(maxPerWindow: 1, TimeSpan.FromMinutes(1));
        var worker = new OutboundDeliveryWorker(
            repository, spool, provider, rateLimiter: rateLimiter, utcNowProvider: () => clock.Now);

        // Simulate several idle polls of an empty queue, each a few seconds apart (well within
        // the same rolling window). None of these should spend the single available token.
        for (var i = 0; i < 5; i++)
        {
            Assert.False(await worker.ProcessNextAsync(Ct));
            clock.Now += TimeSpan.FromSeconds(5);
        }

        // A message now arrives. Because idle polling never touched the budget, this first real
        // submission must still be allowed immediately, not deferred by phantom acquisitions.
        await EnqueueOnlyAsync(repository, spool, "sender@example.com", ["rcpt@example.com"]);

        Assert.True(await worker.ProcessNextAsync(Ct));
        Assert.Equal(1, submittedCount);
    }

    [Fact]
    public async Task ProcessNextAsync_ItemDiscardedByAdminMidAttempt_RetryableOutcomeDoesNotResurrectIt()
    {
        var (repository, spool, item) = await EnqueueItemAsync(
            "sender@example.com", ["rcpt@example.com"]);

        // Simulate an operator running `queue discard` from a second process (its own repository
        // instance on the same shared WAL database) while the worker holds the lease and the
        // network Submit is in flight. The discard commits mid-attempt; the worker then persists a
        // RetryableFailure outcome.
        var adminRepository = new SqliteQueueRepository(_dbPath);
        var provider = new FakeOutboundProvider(
            _ => new Dictionary<string, SubmitOutcome>
            {
                ["rcpt@example.com"] = new SubmitOutcome(OutboundSubmitResult.RetryableFailure),
            },
            (_, _) => adminRepository.DiscardAsync(item.Id, Ct).GetAwaiter().GetResult());
        var worker = new OutboundDeliveryWorker(repository, spool, provider);

        var processed = await worker.ProcessNextAsync(Ct);

        Assert.True(processed);
        var reloaded = await repository.GetByIdAsync(item.Id, Ct);
        // The admin's discard must win: the worker's post-attempt re-derivation and retry
        // scheduling must not flip Discarded back to RetryScheduled or set a future NextAttemptUtc.
        Assert.Equal(QueueItemStatus.Discarded, reloaded!.Status);
        Assert.Null(reloaded.NextAttemptUtc);

        // And the discarded mail must never be re-leased for a later delivery attempt.
        var released = await repository.TryLeaseNextAsync("owner", TimeSpan.FromMinutes(5), Ct);
        Assert.Null(released);
    }

    [Fact]
    public async Task ProcessNextAsync_SpoolFileMissing_MarksItemPoisonWithoutThrowing()
    {
        var (repository, spool, item) = await EnqueueItemAsync(
            "sender@example.com", ["a@example.com", "b@example.com"]);

        // The spool file disappears after enqueue (AV quarantine, manual cleanup, ...), so the
        // spool read raises FileNotFoundException - a deterministic, permanent data error.
        File.Delete(item.MimePath);

        var provider = new FakeOutboundProvider(envelope => envelope.Recipients
            .ToDictionary(r => r, _ => new SubmitOutcome(OutboundSubmitResult.Success)));
        var worker = new OutboundDeliveryWorker(repository, spool, provider);

        // Must not throw: an escaping exception would trip StopHost and kill the whole gateway.
        var processed = await worker.ProcessNextAsync(Ct);

        Assert.True(processed);
        var reloaded = await repository.GetByIdAsync(item.Id, Ct);
        Assert.Equal(QueueItemStatus.Poison, reloaded!.Status);
        Assert.All(reloaded.Recipients, r => Assert.Equal(RecipientStatus.PermanentlyFailed, r.Status));

        // Poison is a dead end - it must never be re-leased.
        var released = await repository.TryLeaseNextAsync("owner", TimeSpan.FromMinutes(5), Ct);
        Assert.Null(released);
    }

    [Fact]
    public async Task ProcessNextAsync_ProviderThrowsTransientError_DoesNotThrowAndLeavesItemLeasedForRetry()
    {
        var (repository, spool, item) = await EnqueueItemAsync(
            "sender@example.com", ["a@example.com"]);

        // A transient infrastructure error (here an IOException) surfaces from the provider.
        var provider = new FakeOutboundProvider(_ => throw new IOException("transient network glitch"));
        var worker = new OutboundDeliveryWorker(repository, spool, provider);

        var processed = await worker.ProcessNextAsync(Ct);

        Assert.True(processed);
        var reloaded = await repository.GetByIdAsync(item.Id, Ct);
        // Left leased with no retry scheduled: the periodic lease-recovery sweep will re-queue it
        // for a later attempt. It is neither poisoned nor lost, and the host is not stopped.
        Assert.Equal(QueueItemStatus.Leased, reloaded!.Status);
        Assert.Null(reloaded.NextAttemptUtc);
    }

    private int _enqueueSequence;

    private async Task<QueueItem> EnqueueOnlyAsync(
        SqliteQueueRepository repository, FileSpool spool, string mailFrom, string[] recipients)
    {
        var key = Guid.NewGuid();
        var bytes = BuildRawMime(mailFrom, recipients);
        var writeResult = await spool.WriteAsync(key, bytes, Ct);

        var envelope = new Envelope(mailFrom, recipients);
        // Strictly increasing CreatedAtUtc (not just DateTimeOffset.UtcNow, whose resolution can
        // coincide across a tight loop) so TryLeaseNextAsync's "ORDER BY CreatedAtUtc" makes lease
        // order deterministic for tests that enqueue several items back-to-back.
        var now = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(_enqueueSequence++);
        var item = new QueueItem
        {
            Id = key,
            Envelope = envelope,
            Recipients = recipients.Select(r => new RecipientDelivery(r)).ToList(),
            MimePath = writeResult.Path,
            Hash = writeResult.Hash,
            SizeBytes = writeResult.SizeBytes,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Status = QueueItemStatus.Queued,
        };
        await repository.EnqueueAsync(item, Ct);
        return item;
    }

    private sealed class MutableClock(DateTimeOffset initial)
    {
        public DateTimeOffset Now { get; set; } = initial;
    }

    private sealed class FakeOutboundProvider(
        Func<Envelope, Dictionary<string, SubmitOutcome>> resultFactory,
        Action<Envelope, byte[]>? onSubmit = null) : IOutboundProvider
    {
        public Task<IReadOnlyDictionary<string, SubmitOutcome>> Submit(
            Envelope envelope, byte[] rawMime, CancellationToken ct)
        {
            onSubmit?.Invoke(envelope, rawMime);
            IReadOnlyDictionary<string, SubmitOutcome> results = resultFactory(envelope);
            return Task.FromResult(results);
        }
    }
}
