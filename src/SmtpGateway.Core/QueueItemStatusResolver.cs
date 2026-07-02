namespace SmtpGateway.Core;

/// <summary>
/// Derives the overall <see cref="QueueItemStatus"/> purely from the current set of
/// per-recipient <see cref="RecipientDelivery"/> statuses. Scoped to the statuses that
/// recipient state actually determines: Queued, PartiallySent, Sent, RetryScheduled and
/// Poison. Leased/Sending/Expired are set elsewhere by lease and TTL logic and are never
/// returned by this resolver.
/// </summary>
public static class QueueItemStatusResolver
{
    public static QueueItemStatus Derive(IReadOnlyCollection<RecipientDelivery> recipients)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        if (recipients.Count == 0)
        {
            throw new ArgumentException("At least one recipient delivery is required.", nameof(recipients));
        }

        var anyPending = false;
        var anySent = false;
        var anyRetryable = false;
        var anyFailed = false;

        foreach (var recipient in recipients)
        {
            switch (recipient.Status)
            {
                case RecipientStatus.Pending:
                    anyPending = true;
                    break;
                case RecipientStatus.Sent:
                    anySent = true;
                    break;
                case RecipientStatus.Retryable:
                    anyRetryable = true;
                    break;
                case RecipientStatus.PermanentlyFailed:
                    anyFailed = true;
                    break;
            }
        }

        // Some recipients haven't had a final or retryable outcome yet, so the item is
        // still in flight and not yet resolved.
        if (anyPending)
        {
            return QueueItemStatus.Queued;
        }

        if (anySent)
        {
            // Every recipient was sent, no leftovers -> fully Sent. Otherwise some
            // recipients still need a retry or have permanently failed -> PartiallySent.
            return anyRetryable || anyFailed ? QueueItemStatus.PartiallySent : QueueItemStatus.Sent;
        }

        // Nothing was ever sent. As long as anything remains retryable there is still
        // automated work to do, regardless of any permanently-failed recipients mixed in.
        if (anyRetryable)
        {
            return QueueItemStatus.RetryScheduled;
        }

        // Nothing pending, nothing sent, nothing retryable: every recipient permanently
        // failed. There is no further automated action possible, so the item is dead and
        // needs operator attention - that is exactly what Poison represents.
        return QueueItemStatus.Poison;
    }
}
