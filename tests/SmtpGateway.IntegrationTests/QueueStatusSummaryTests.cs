using SmtpGateway.Admin.Tui;
using SmtpGateway.Core;
using Xunit;

namespace SmtpGateway.IntegrationTests;

/// <summary>
/// Pure logic tests for the status dashboard's aggregation - no SQLite/file spool involved.
/// </summary>
public sealed class QueueStatusSummaryTests
{
    private static QueueItem CreateItem(
        QueueItemStatus status,
        long sizeBytes = 100,
        int attemptCount = 0,
        DateTimeOffset? createdAtUtc = null,
        RecipientStatus[]? recipientStatuses = null)
    {
        var now = createdAtUtc ?? DateTimeOffset.UtcNow;
        var statuses = recipientStatuses ?? [RecipientStatus.Pending];
        var recipients = statuses
            .Select((s, i) => new RecipientDelivery($"r{i}@example.com", s))
            .ToList();

        return new QueueItem
        {
            Id = Guid.NewGuid(),
            Envelope = new Envelope("sender@example.com", recipients.Select(r => r.Address)),
            Recipients = recipients,
            MimePath = @"C:\spool\msg.eml",
            Hash = "deadbeef",
            SizeBytes = sizeBytes,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            AttemptCount = attemptCount,
            Status = status,
        };
    }

    [Fact]
    public void Build_EmptyList_AllCountsZeroAndNoOldestAge()
    {
        var summary = QueueStatusSummary.Build([], DateTimeOffset.UtcNow);

        Assert.All(Enum.GetValues<QueueItemStatus>(), status => Assert.Equal(0, summary.CountsByStatus[status]));
        Assert.Null(summary.OldestQueuedAge);
        Assert.Equal(0, summary.TotalSpoolBytes);
        Assert.Equal(0, summary.TotalAttempts);
        Assert.Equal(0, summary.RecipientsSent);
        Assert.Equal(0, summary.RecipientsPermanentlyFailed);
        Assert.Equal(0, summary.PoisonCount);
    }

    [Fact]
    public void Build_CountsItemsByStatus()
    {
        var items = new[]
        {
            CreateItem(QueueItemStatus.Queued),
            CreateItem(QueueItemStatus.Queued),
            CreateItem(QueueItemStatus.Poison),
            CreateItem(QueueItemStatus.Discarded),
        };

        var summary = QueueStatusSummary.Build(items, DateTimeOffset.UtcNow);

        Assert.Equal(2, summary.CountsByStatus[QueueItemStatus.Queued]);
        Assert.Equal(1, summary.CountsByStatus[QueueItemStatus.Poison]);
        Assert.Equal(1, summary.CountsByStatus[QueueItemStatus.Discarded]);
        Assert.Equal(1, summary.PoisonCount);
    }

    [Fact]
    public void Build_OldestQueuedAge_OnlyConsidersQueuedStatusAndPicksOldest()
    {
        var now = DateTimeOffset.UtcNow;
        var items = new[]
        {
            CreateItem(QueueItemStatus.Queued, createdAtUtc: now - TimeSpan.FromMinutes(5)),
            CreateItem(QueueItemStatus.Queued, createdAtUtc: now - TimeSpan.FromMinutes(30)),
            // A much older item that is NOT Queued must not affect the oldest-queued-age figure.
            CreateItem(QueueItemStatus.Sent, createdAtUtc: now - TimeSpan.FromDays(10)),
        };

        var summary = QueueStatusSummary.Build(items, now);

        Assert.NotNull(summary.OldestQueuedAge);
        Assert.True(summary.OldestQueuedAge!.Value >= TimeSpan.FromMinutes(30));
        Assert.True(summary.OldestQueuedAge!.Value < TimeSpan.FromMinutes(31));
    }

    [Fact]
    public void Build_SumsSpoolBytesAndAttemptsAcrossAllItems()
    {
        var items = new[]
        {
            CreateItem(QueueItemStatus.Queued, sizeBytes: 100, attemptCount: 1),
            CreateItem(QueueItemStatus.RetryScheduled, sizeBytes: 250, attemptCount: 3),
        };

        var summary = QueueStatusSummary.Build(items, DateTimeOffset.UtcNow);

        Assert.Equal(350, summary.TotalSpoolBytes);
        Assert.Equal(4, summary.TotalAttempts);
    }

    [Fact]
    public void Build_CountsSentAndPermanentlyFailedRecipientsAcrossAllItems()
    {
        var items = new[]
        {
            CreateItem(
                QueueItemStatus.PartiallySent,
                recipientStatuses: [RecipientStatus.Sent, RecipientStatus.PermanentlyFailed]),
            CreateItem(QueueItemStatus.Sent, recipientStatuses: [RecipientStatus.Sent]),
        };

        var summary = QueueStatusSummary.Build(items, DateTimeOffset.UtcNow);

        Assert.Equal(2, summary.RecipientsSent);
        Assert.Equal(1, summary.RecipientsPermanentlyFailed);
    }
}
