using Microsoft.Data.Sqlite;
using SmtpGateway.Core;
using SmtpGateway.Infrastructure;
using Xunit;

namespace SmtpGateway.Infrastructure.Tests;

public sealed class SqliteQueueRepositoryTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dbPath;

    public SqliteQueueRepositoryTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(), "SmtpGateway.QueueRepositoryTests", $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
    }

    public void Dispose()
    {
        // Microsoft.Data.Sqlite pools native connections by default, which can keep the file
        // handle open past our using blocks; clear this test's own pool (scoped by connection
        // string) before deleting the temp file. Using the process-global ClearAllPools() here
        // would race with other test classes concurrently opening/closing pooled connections
        // for their own unrelated databases.
        using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath }.ConnectionString))
        {
            SqliteConnection.ClearPool(connection);
        }

        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static QueueItem CreateItem(
        string? mailFrom = null,
        string[]? recipients = null,
        QueueItemStatus status = QueueItemStatus.Queued,
        DateTimeOffset? nextAttemptUtc = null,
        DateTimeOffset? createdAtUtc = null,
        long sizeBytes = 1234)
    {
        var addresses = recipients ?? new[] { "rcpt1@example.com", "rcpt2@example.com" };
        var envelope = new Envelope(mailFrom ?? "sender@example.com", addresses);
        var now = createdAtUtc ?? DateTimeOffset.UtcNow;

        return new QueueItem
        {
            Id = Guid.NewGuid(),
            Envelope = envelope,
            Recipients = addresses.Select(a => new RecipientDelivery(a)).ToList(),
            MimePath = @"C:\spool\msg1.eml",
            Hash = "deadbeef",
            SizeBytes = sizeBytes,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            AttemptCount = 0,
            NextAttemptUtc = nextAttemptUtc,
            LeaseOwner = null,
            LeaseExpiryUtc = null,
            LastError = null,
            Status = status,
        };
    }

    [Fact]
    public async Task EnqueueAsync_ThenGetByIdAsync_RoundTripsAllFields()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var item = CreateItem();

        await repository.EnqueueAsync(item, Ct);
        var loaded = await repository.GetByIdAsync(item.Id, Ct);

        Assert.NotNull(loaded);
        Assert.Equal(item.Id, loaded!.Id);
        Assert.Equal(item.Envelope.MailFrom, loaded.Envelope.MailFrom);
        Assert.Equal(item.Envelope.Recipients, loaded.Envelope.Recipients);
        Assert.Equal(item.MimePath, loaded.MimePath);
        Assert.Equal(item.Hash, loaded.Hash);
        Assert.Equal(item.SizeBytes, loaded.SizeBytes);
        Assert.Equal(item.CreatedAtUtc, loaded.CreatedAtUtc);
        Assert.Equal(item.UpdatedAtUtc, loaded.UpdatedAtUtc);
        Assert.Equal(item.AttemptCount, loaded.AttemptCount);
        Assert.Equal(item.NextAttemptUtc, loaded.NextAttemptUtc);
        Assert.Equal(item.LeaseOwner, loaded.LeaseOwner);
        Assert.Equal(item.LeaseExpiryUtc, loaded.LeaseExpiryUtc);
        Assert.Equal(item.LastError, loaded.LastError);
        Assert.Equal(item.Status, loaded.Status);

        Assert.Equal(item.Recipients.Count, loaded.Recipients.Count);
        for (var i = 0; i < item.Recipients.Count; i++)
        {
            Assert.Equal(item.Recipients[i].Address, loaded.Recipients[i].Address);
            Assert.Equal(item.Recipients[i].Status, loaded.Recipients[i].Status);
            Assert.Equal(item.Recipients[i].AttemptCount, loaded.Recipients[i].AttemptCount);
            Assert.Equal(item.Recipients[i].LastError, loaded.Recipients[i].LastError);
        }
    }

    [Fact]
    public async Task TryLeaseNextAsync_ConcurrentCallers_OnlyOneClaimsTheSingleQueuedItem()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var item = CreateItem();
        await repository.EnqueueAsync(item, Ct);

        var results = await Task.WhenAll(
            repository.TryLeaseNextAsync("owner-a", TimeSpan.FromMinutes(5), Ct),
            repository.TryLeaseNextAsync("owner-b", TimeSpan.FromMinutes(5), Ct));

        var claimed = results.Where(r => r is not null).ToList();
        Assert.Single(claimed);
        Assert.Equal(item.Id, claimed[0]!.Id);
        Assert.Equal(QueueItemStatus.Leased, claimed[0]!.Status);

        var reloaded = await repository.GetByIdAsync(item.Id, Ct);
        Assert.Equal(QueueItemStatus.Leased, reloaded!.Status);
        Assert.True(reloaded.LeaseOwner is "owner-a" or "owner-b");
    }

    [Fact]
    public async Task TryLeaseNextAsync_NoEligibleItem_ReturnsNull()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var item = CreateItem(nextAttemptUtc: DateTimeOffset.UtcNow.AddHours(1));
        await repository.EnqueueAsync(item, Ct);

        var result = await repository.TryLeaseNextAsync("owner-a", TimeSpan.FromMinutes(5), Ct);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryLeaseNextAsync_PartiallySentWithRetryableRecipient_IsReLeased()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var now = DateTimeOffset.UtcNow;
        var item = new QueueItem
        {
            Id = Guid.NewGuid(),
            Envelope = new Envelope("sender@example.com", ["sent@example.com", "retry@example.com"]),
            Recipients =
            [
                new RecipientDelivery("sent@example.com", RecipientStatus.Sent, attemptCount: 1),
                new RecipientDelivery("retry@example.com", RecipientStatus.Retryable, attemptCount: 1, lastError: "temporary failure"),
            ],
            MimePath = @"C:\spool\msg1.eml",
            Hash = "deadbeef",
            SizeBytes = 1234,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            NextAttemptUtc = now.AddMinutes(-1),
            Status = QueueItemStatus.PartiallySent,
        };
        await repository.EnqueueAsync(item, Ct);

        var leased = await repository.TryLeaseNextAsync("owner-a", TimeSpan.FromMinutes(5), Ct);

        Assert.NotNull(leased);
        Assert.Equal(item.Id, leased!.Id);
        Assert.Equal(QueueItemStatus.Leased, leased.Status);
    }

    [Fact]
    public async Task TryLeaseNextAsync_PartiallySentWithNoRetryableRecipient_IsNotLeased()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var now = DateTimeOffset.UtcNow;
        var item = new QueueItem
        {
            Id = Guid.NewGuid(),
            Envelope = new Envelope("sender@example.com", ["sent@example.com", "bad@example.com"]),
            Recipients =
            [
                new RecipientDelivery("sent@example.com", RecipientStatus.Sent, attemptCount: 1),
                new RecipientDelivery("bad@example.com", RecipientStatus.PermanentlyFailed, attemptCount: 1, lastError: "rejected"),
            ],
            MimePath = @"C:\spool\msg1.eml",
            Hash = "deadbeef",
            SizeBytes = 1234,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Status = QueueItemStatus.PartiallySent,
        };
        await repository.EnqueueAsync(item, Ct);

        var leased = await repository.TryLeaseNextAsync("owner-a", TimeSpan.FromMinutes(5), Ct);

        Assert.Null(leased);
    }

    [Fact]
    public async Task UpdateRecipientStatusAsync_BothRecipientsSent_FlipsOverallStatusToSent()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var item = CreateItem(recipients: new[] { "a@example.com", "b@example.com" });
        await repository.EnqueueAsync(item, Ct);

        await repository.UpdateRecipientStatusAsync(item.Id, "a@example.com", RecipientStatus.Sent, 1, null, Ct);
        await repository.UpdateRecipientStatusAsync(item.Id, "b@example.com", RecipientStatus.Sent, 1, null, Ct);

        var loaded = await repository.GetByIdAsync(item.Id, Ct);
        Assert.Equal(QueueItemStatus.Sent, loaded!.Status);
    }

    [Fact]
    public async Task UpdateRecipientStatusAsync_OneSentOneRetryable_FlipsOverallStatusToPartiallySent()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var item = CreateItem(recipients: new[] { "a@example.com", "b@example.com" });
        await repository.EnqueueAsync(item, Ct);

        await repository.UpdateRecipientStatusAsync(item.Id, "a@example.com", RecipientStatus.Sent, 1, null, Ct);
        await repository.UpdateRecipientStatusAsync(
            item.Id, "b@example.com", RecipientStatus.Retryable, 1, "temporary failure", Ct);

        var loaded = await repository.GetByIdAsync(item.Id, Ct);
        Assert.Equal(QueueItemStatus.PartiallySent, loaded!.Status);
        var recipientB = loaded.Recipients.Single(r => r.Address == "b@example.com");
        Assert.Equal(RecipientStatus.Retryable, recipientB.Status);
        Assert.Equal("temporary failure", recipientB.LastError);
    }

    [Fact]
    public async Task ReleaseExpiredLeasesAsync_ResetsExpiredLeaseBackToQueued()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var item = CreateItem();
        await repository.EnqueueAsync(item, Ct);

        var leased = await repository.TryLeaseNextAsync("owner-a", TimeSpan.FromMilliseconds(1), Ct);
        Assert.NotNull(leased);
        await Task.Delay(50, Ct);

        var releasedCount = await repository.ReleaseExpiredLeasesAsync(Ct);

        Assert.Equal(1, releasedCount);
        var reloaded = await repository.GetByIdAsync(item.Id, Ct);
        Assert.Equal(QueueItemStatus.Queued, reloaded!.Status);
        Assert.Null(reloaded.LeaseOwner);
        Assert.Null(reloaded.LeaseExpiryUtc);
    }

    [Fact]
    public async Task ExpireOverdueAsync_ItemPastTtl_TransitionsToExpired()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var item = CreateItem(createdAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10), status: QueueItemStatus.Queued);
        await repository.EnqueueAsync(item, Ct);

        var count = await repository.ExpireOverdueAsync(TimeSpan.FromMinutes(5), Ct);

        Assert.Equal(1, count);
        var reloaded = await repository.GetByIdAsync(item.Id, Ct);
        Assert.Equal(QueueItemStatus.Expired, reloaded!.Status);
    }

    [Fact]
    public async Task ExpireOverdueAsync_SentItemPastTtl_IsNotTouched()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var item = CreateItem(
            createdAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10),
            status: QueueItemStatus.Sent,
            recipients: new[] { "a@example.com" });
        await repository.EnqueueAsync(item, Ct);

        var count = await repository.ExpireOverdueAsync(TimeSpan.FromMinutes(5), Ct);

        Assert.Equal(0, count);
        var reloaded = await repository.GetByIdAsync(item.Id, Ct);
        Assert.Equal(QueueItemStatus.Sent, reloaded!.Status);
    }

    [Fact]
    public async Task ExpireOverdueAsync_ItemWellWithinTtl_IsNotTouched()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var item = CreateItem(createdAtUtc: DateTimeOffset.UtcNow, status: QueueItemStatus.Queued);
        await repository.EnqueueAsync(item, Ct);

        var count = await repository.ExpireOverdueAsync(TimeSpan.FromMinutes(5), Ct);

        Assert.Equal(0, count);
        var reloaded = await repository.GetByIdAsync(item.Id, Ct);
        Assert.Equal(QueueItemStatus.Queued, reloaded!.Status);
    }

    [Fact]
    public async Task ExpireOverdueAsync_MultipleOverdueItems_ReturnsMatchingCount()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var overdue1 = CreateItem(createdAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10), status: QueueItemStatus.Queued);
        var overdue2 = CreateItem(
            createdAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10), status: QueueItemStatus.PartiallySent);
        var withinTtl = CreateItem(createdAtUtc: DateTimeOffset.UtcNow, status: QueueItemStatus.Queued);
        await repository.EnqueueAsync(overdue1, Ct);
        await repository.EnqueueAsync(overdue2, Ct);
        await repository.EnqueueAsync(withinTtl, Ct);

        var count = await repository.ExpireOverdueAsync(TimeSpan.FromMinutes(5), Ct);

        Assert.Equal(2, count);
        Assert.Equal(QueueItemStatus.Expired, (await repository.GetByIdAsync(overdue1.Id, Ct))!.Status);
        Assert.Equal(QueueItemStatus.Expired, (await repository.GetByIdAsync(overdue2.Id, Ct))!.Status);
        Assert.Equal(QueueItemStatus.Queued, (await repository.GetByIdAsync(withinTtl.Id, Ct))!.Status);
    }

    [Fact]
    public async Task SetNextAttemptAsync_UpdatesAttemptCountAndNextAttemptUtc()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var item = CreateItem();
        await repository.EnqueueAsync(item, Ct);
        var nextAttempt = DateTimeOffset.UtcNow.AddMinutes(5);

        await repository.SetNextAttemptAsync(item.Id, 2, nextAttempt, Ct);

        var reloaded = await repository.GetByIdAsync(item.Id, Ct);
        Assert.Equal(2, reloaded!.AttemptCount);
        Assert.Equal(nextAttempt, reloaded.NextAttemptUtc);
    }

    [Fact]
    public async Task ListAsync_ReturnsAllEnqueuedItems()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var item1 = CreateItem();
        var item2 = CreateItem();
        await repository.EnqueueAsync(item1, Ct);
        await repository.EnqueueAsync(item2, Ct);

        var all = await repository.ListAsync(Ct);

        Assert.Equal(2, all.Count);
        Assert.Contains(all, i => i.Id == item1.Id);
        Assert.Contains(all, i => i.Id == item2.Id);
    }

    [Fact]
    public async Task DiscardAsync_SetsStatusToDiscarded_ItemRemainsReadable()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var item = CreateItem(status: QueueItemStatus.Poison);
        await repository.EnqueueAsync(item, Ct);

        await repository.DiscardAsync(item.Id, Ct);

        var reloaded = await repository.GetByIdAsync(item.Id, Ct);
        Assert.NotNull(reloaded);
        Assert.Equal(QueueItemStatus.Discarded, reloaded!.Status);

        var listed = await repository.ListAsync(Ct);
        Assert.Contains(listed, i => i.Id == item.Id && i.Status == QueueItemStatus.Discarded);
    }

    [Fact]
    public async Task DiscardAsync_UnknownItem_Throws()
    {
        var repository = new SqliteQueueRepository(_dbPath);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.DiscardAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task TryLeaseNextAsync_DiscardedItem_IsNeverLeased()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var item = CreateItem(status: QueueItemStatus.Poison);
        await repository.EnqueueAsync(item, Ct);
        await repository.DiscardAsync(item.Id, Ct);

        var leased = await repository.TryLeaseNextAsync("owner-a", TimeSpan.FromMinutes(5), Ct);

        Assert.Null(leased);
        var reloaded = await repository.GetByIdAsync(item.Id, Ct);
        Assert.Equal(QueueItemStatus.Discarded, reloaded!.Status);
    }

    [Fact]
    public async Task RetryAsync_PoisonItem_ResetsRecipientsToRetryableAndBecomesLeasable()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var now = DateTimeOffset.UtcNow;
        var item = new QueueItem
        {
            Id = Guid.NewGuid(),
            Envelope = new Envelope("sender@example.com", ["a@example.com", "b@example.com"]),
            Recipients =
            [
                new RecipientDelivery("a@example.com", RecipientStatus.PermanentlyFailed, attemptCount: 3, lastError: "rejected"),
                new RecipientDelivery("b@example.com", RecipientStatus.PermanentlyFailed, attemptCount: 3, lastError: "rejected"),
            ],
            MimePath = @"C:\spool\msg1.eml",
            Hash = "deadbeef",
            SizeBytes = 1234,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            NextAttemptUtc = null,
            Status = QueueItemStatus.Poison,
        };
        await repository.EnqueueAsync(item, Ct);

        await repository.RetryAsync(item.Id, Ct);

        var reloaded = await repository.GetByIdAsync(item.Id, Ct);
        Assert.True(
            reloaded!.Status is QueueItemStatus.Queued or QueueItemStatus.RetryScheduled,
            $"Expected Queued or RetryScheduled but was {reloaded.Status}");
        Assert.Null(reloaded.NextAttemptUtc);
        Assert.All(reloaded.Recipients, r =>
        {
            Assert.Equal(RecipientStatus.Retryable, r.Status);
            Assert.Equal(0, r.AttemptCount);
        });

        var leased = await repository.TryLeaseNextAsync("owner-a", TimeSpan.FromMinutes(5), Ct);
        Assert.NotNull(leased);
        Assert.Equal(item.Id, leased!.Id);
    }

    [Fact]
    public async Task RetryAsync_PartiallySentItem_LeavesSentRecipientUntouchedAndResetsFailedOne()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var now = DateTimeOffset.UtcNow;
        var item = new QueueItem
        {
            Id = Guid.NewGuid(),
            Envelope = new Envelope("sender@example.com", ["sent@example.com", "bad@example.com"]),
            Recipients =
            [
                new RecipientDelivery("sent@example.com", RecipientStatus.Sent, attemptCount: 1),
                new RecipientDelivery("bad@example.com", RecipientStatus.PermanentlyFailed, attemptCount: 2, lastError: "rejected"),
            ],
            MimePath = @"C:\spool\msg1.eml",
            Hash = "deadbeef",
            SizeBytes = 1234,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Status = QueueItemStatus.PartiallySent,
        };
        await repository.EnqueueAsync(item, Ct);

        await repository.RetryAsync(item.Id, Ct);

        var reloaded = await repository.GetByIdAsync(item.Id, Ct);
        var sentRecipient = reloaded!.Recipients.Single(r => r.Address == "sent@example.com");
        var badRecipient = reloaded.Recipients.Single(r => r.Address == "bad@example.com");
        Assert.Equal(RecipientStatus.Sent, sentRecipient.Status);
        Assert.Equal(1, sentRecipient.AttemptCount);
        Assert.Equal(RecipientStatus.Retryable, badRecipient.Status);
        Assert.Equal(0, badRecipient.AttemptCount);
        Assert.Equal(QueueItemStatus.PartiallySent, reloaded.Status);

        // Re-leasing and simulating a delivery attempt must never resend to the already-Sent
        // recipient - this mirrors OutboundDeliveryWorker's own behavior of only submitting to
        // non-Sent recipients.
        var leased = await repository.TryLeaseNextAsync("owner-a", TimeSpan.FromMinutes(5), Ct);
        Assert.NotNull(leased);
        var recipientsToSend = leased!.Recipients.Where(r => r.Status != RecipientStatus.Sent).ToList();
        Assert.Single(recipientsToSend);
        Assert.Equal("bad@example.com", recipientsToSend[0].Address);
    }

    [Fact]
    public async Task RetryAsync_UnknownItem_Throws()
    {
        var repository = new SqliteQueueRepository(_dbPath);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.RetryAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task GetTotalSpoolBytesAsync_NoItems_ReturnsZero()
    {
        var repository = new SqliteQueueRepository(_dbPath);

        var total = await repository.GetTotalSpoolBytesAsync(Ct);

        Assert.Equal(0, total);
    }

    [Fact]
    public async Task GetTotalSpoolBytesAsync_SumsSizeBytesAcrossAllStatusesRegardlessOfState()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var queued = CreateItem(status: QueueItemStatus.Queued, sizeBytes: 1000);
        var sent = CreateItem(status: QueueItemStatus.Sent, sizeBytes: 2000);
        var poison = CreateItem(status: QueueItemStatus.Poison, sizeBytes: 3000);
        await repository.EnqueueAsync(queued, Ct);
        await repository.EnqueueAsync(sent, Ct);
        await repository.EnqueueAsync(poison, Ct);
        await repository.DiscardAsync(poison.Id, Ct);

        var total = await repository.GetTotalSpoolBytesAsync(Ct);

        Assert.Equal(6000, total);
    }

    [Fact]
    public async Task SetNextAttemptAsync_DiscardedItem_DoesNotRescheduleOrOverwriteStatus()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var item = CreateItem(status: QueueItemStatus.Poison);
        await repository.EnqueueAsync(item, Ct);
        await repository.DiscardAsync(item.Id, Ct);

        // The worker (holding a stale in-flight lease) tries to schedule a retry after the discard
        // already committed. The state guard must make this a no-op.
        await repository.SetNextAttemptAsync(item.Id, 3, DateTimeOffset.UtcNow.AddMinutes(-1), Ct);

        var reloaded = await repository.GetByIdAsync(item.Id, Ct);
        Assert.Equal(QueueItemStatus.Discarded, reloaded!.Status);
        Assert.Null(reloaded.NextAttemptUtc);
    }

    [Fact]
    public async Task UpdateRecipientStatusAsync_DiscardedItem_RecordsRecipientHistoryButKeepsStatusDiscarded()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var item = CreateItem(recipients: new[] { "a@example.com" }, status: QueueItemStatus.Poison);
        await repository.EnqueueAsync(item, Ct);
        await repository.DiscardAsync(item.Id, Ct);

        await repository.UpdateRecipientStatusAsync(item.Id, "a@example.com", RecipientStatus.Retryable, 1, "temp", Ct);

        var reloaded = await repository.GetByIdAsync(item.Id, Ct);
        // Recipient-level history is still written...
        Assert.Equal(RecipientStatus.Retryable, reloaded!.Recipients.Single().Status);
        // ...but the derived overall status must not clobber the administrator's Discarded state.
        Assert.Equal(QueueItemStatus.Discarded, reloaded.Status);
    }

    [Fact]
    public async Task RetryAsync_ResetsItemLevelAttemptCountToZero()
    {
        var repository = new SqliteQueueRepository(_dbPath);
        var now = DateTimeOffset.UtcNow;
        var item = new QueueItem
        {
            Id = Guid.NewGuid(),
            Envelope = new Envelope("sender@example.com", ["a@example.com"]),
            Recipients =
            [
                new RecipientDelivery("a@example.com", RecipientStatus.PermanentlyFailed, attemptCount: 5, lastError: "rejected"),
            ],
            MimePath = @"C:\spool\msg1.eml",
            Hash = "deadbeef",
            SizeBytes = 1234,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            AttemptCount = 10,
            NextAttemptUtc = null,
            Status = QueueItemStatus.Poison,
        };
        await repository.EnqueueAsync(item, Ct);

        await repository.RetryAsync(item.Id, Ct);

        var reloaded = await repository.GetByIdAsync(item.Id, Ct);
        // The item-level attempt count drives RetryPolicy.GetDelay; a manual retry restarts the
        // backoff cadence from the beginning, so it must be reset to 0 (not left at 10, which would
        // make a failed manual retry wait the mature 1-hour delay).
        Assert.Equal(0, reloaded!.AttemptCount);
    }
}
