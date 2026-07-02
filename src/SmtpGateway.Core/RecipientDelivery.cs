namespace SmtpGateway.Core;

/// <summary>
/// The delivery state of a single recipient of a queued mail.
/// </summary>
public sealed record RecipientDelivery
{
    public string Address { get; }

    public RecipientStatus Status { get; init; }

    public int AttemptCount { get; init; }

    public string? LastError { get; init; }

    public RecipientDelivery(
        string address,
        RecipientStatus status = RecipientStatus.Pending,
        int attemptCount = 0,
        string? lastError = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        Address = address;
        Status = status;
        AttemptCount = attemptCount;
        LastError = lastError;
    }
}
