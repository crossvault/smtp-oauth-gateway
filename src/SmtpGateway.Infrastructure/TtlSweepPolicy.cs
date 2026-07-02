namespace SmtpGateway.Infrastructure;

/// <summary>
/// Pure timing decision for how often the outbound delivery loop runs
/// <see cref="SqliteQueueRepository.ExpireOverdueAsync"/>: no timer/thread of its own, just
/// "has enough time passed since the last sweep" for a caller's own poll loop to check on every
/// iteration.
/// </summary>
public static class TtlSweepPolicy
{
    /// <summary>
    /// True once at least <paramref name="interval"/> has elapsed since <paramref name="lastSweepUtc"/>.
    /// </summary>
    public static bool IsDue(DateTimeOffset lastSweepUtc, DateTimeOffset nowUtc, TimeSpan interval) =>
        nowUtc - lastSweepUtc >= interval;
}
