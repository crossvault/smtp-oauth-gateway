using SmtpGateway.Core;

namespace SmtpGateway.Admin.Tui;

/// <summary>
/// Pure aggregation of the status dashboard's numbers from an already-loaded list of queue
/// items - no I/O of its own, so it is testable without a real SQLite database or file spool.
/// </summary>
public sealed record QueueStatusSummary(
    IReadOnlyDictionary<QueueItemStatus, int> CountsByStatus,
    TimeSpan? OldestQueuedAge,
    long TotalSpoolBytes,
    int TotalAttempts,
    int RecipientsSent,
    int RecipientsPermanentlyFailed,
    int PoisonCount)
{
    public static QueueStatusSummary Build(IReadOnlyList<QueueItem> items, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(items);

        var countsByStatus = Enum.GetValues<QueueItemStatus>().ToDictionary(status => status, _ => 0);
        long totalSpoolBytes = 0;
        var totalAttempts = 0;
        var recipientsSent = 0;
        var recipientsPermanentlyFailed = 0;
        DateTimeOffset? oldestQueuedCreatedAt = null;

        foreach (var item in items)
        {
            countsByStatus[item.Status]++;
            totalSpoolBytes += item.SizeBytes;
            totalAttempts += item.AttemptCount;
            recipientsSent += item.Recipients.Count(r => r.Status == RecipientStatus.Sent);
            recipientsPermanentlyFailed += item.Recipients.Count(r => r.Status == RecipientStatus.PermanentlyFailed);

            if (item.Status == QueueItemStatus.Queued
                && (oldestQueuedCreatedAt is null || item.CreatedAtUtc < oldestQueuedCreatedAt))
            {
                oldestQueuedCreatedAt = item.CreatedAtUtc;
            }
        }

        var oldestQueuedAge = oldestQueuedCreatedAt is { } createdAt ? now - createdAt : (TimeSpan?)null;

        return new QueueStatusSummary(
            countsByStatus,
            oldestQueuedAge,
            totalSpoolBytes,
            totalAttempts,
            recipientsSent,
            recipientsPermanentlyFailed,
            countsByStatus[QueueItemStatus.Poison]);
    }
}
