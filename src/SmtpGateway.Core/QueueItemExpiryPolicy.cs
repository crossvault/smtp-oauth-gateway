namespace SmtpGateway.Core;

/// <summary>
/// Decides whether a queue item has exceeded its time-to-live and should be considered expired.
/// Pure function; callers supply the effective TTL (already capped via
/// <see cref="RetryPolicy.ValidateTtl"/>) and the current time.
/// </summary>
public static class QueueItemExpiryPolicy
{
    /// <summary>
    /// Returns true when <paramref name="nowUtc"/> is at or past <paramref name="createdAtUtc"/>
    /// plus <paramref name="effectiveTtl"/>.
    /// </summary>
    public static bool IsExpired(DateTimeOffset createdAtUtc, TimeSpan effectiveTtl, DateTimeOffset nowUtc) =>
        nowUtc >= createdAtUtc + effectiveTtl;
}
