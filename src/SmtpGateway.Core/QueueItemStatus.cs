namespace SmtpGateway.Core;

/// <summary>
/// The overall lifecycle status of a queued mail (which may have multiple recipients).
/// </summary>
public enum QueueItemStatus
{
    /// <summary>Waiting to be picked up, or currently has at least one recipient still pending.</summary>
    Queued,

    /// <summary>A worker holds an active lease on this item; set by lease logic, not derived from recipient status.</summary>
    Leased,

    /// <summary>A worker is actively submitting this item to the outbound provider.</summary>
    Sending,

    /// <summary>At least one recipient was sent, and at least one other recipient is retryable or permanently failed.</summary>
    PartiallySent,

    /// <summary>Every recipient was sent successfully.</summary>
    Sent,

    /// <summary>No recipient was sent yet, but at least one recipient is still retryable.</summary>
    RetryScheduled,

    /// <summary>Every recipient has permanently failed; the item is dead and requires operator attention.</summary>
    Poison,

    /// <summary>The queue TTL elapsed before the item could be delivered; set by TTL logic, not derived from recipient status.</summary>
    Expired,

    /// <summary>
    /// An administrator explicitly gave up on this item; set directly, never derived from
    /// recipient status. Discarded items remain visible in queue history but are never leased
    /// for further delivery attempts.
    /// </summary>
    Discarded,
}
