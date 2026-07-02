namespace SmtpGateway.Core;

/// <summary>
/// A single queued mail: one envelope, one spooled MIME file, and one delivery record per
/// recipient. Mirrors the persisted SQLite queue row.
/// </summary>
public sealed class QueueItem
{
    public required Guid Id { get; init; }

    public required Envelope Envelope { get; init; }

    public required List<RecipientDelivery> Recipients { get; init; }

    public required string MimePath { get; init; }

    public required string Hash { get; init; }

    public required long SizeBytes { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; set; }

    public int AttemptCount { get; set; }

    public DateTimeOffset? NextAttemptUtc { get; set; }

    public string? LeaseOwner { get; set; }

    public DateTimeOffset? LeaseExpiryUtc { get; set; }

    public string? LastError { get; set; }

    public required QueueItemStatus Status { get; set; }
}
