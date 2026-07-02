namespace SmtpGateway.Core;

/// <summary>
/// The outcome of submitting a mail to an outbound provider.
/// </summary>
public enum OutboundSubmitResult
{
    /// <summary>The provider accepted the mail.</summary>
    Success,

    /// <summary>The provider rejected the mail with a transient error; retry later.</summary>
    RetryableFailure,

    /// <summary>The provider rejected the mail with a non-transient error; do not retry.</summary>
    PermanentFailure,
}

/// <summary>
/// A single recipient's outcome from an <see cref="IOutboundProvider"/> submission, optionally
/// carrying a server-provided retry-after hint (e.g. Graph's 429 Retry-After header) that should
/// take priority over the normal staged <see cref="RetryPolicy"/> backoff when present.
/// </summary>
public readonly record struct SubmitOutcome(OutboundSubmitResult Result, TimeSpan? RetryAfter = null);

/// <summary>
/// Port for submitting a mail to an outbound provider (e.g. Graph/SMTP relay). Because a queue
/// item may have multiple recipients, each recipient can succeed or fail independently, so the
/// result is a per-recipient outcome dictionary rather than a single overall result.
/// </summary>
public interface IOutboundProvider
{
    /// <summary>
    /// Submits the mail to the provider and returns one <see cref="SubmitOutcome"/> per
    /// address in <see cref="Envelope.Recipients"/>, keyed by recipient address.
    /// </summary>
    Task<IReadOnlyDictionary<string, SubmitOutcome>> Submit(Envelope envelope, byte[] rawMime, CancellationToken ct);
}
