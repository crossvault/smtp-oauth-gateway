namespace SmtpGateway.Core;

/// <summary>
/// The retry backoff schedule and queue TTL rules for outbound delivery attempts.
/// </summary>
public static class RetryPolicy
{
    /// <summary>The default queue item time-to-live: 5 days.</summary>
    public static TimeSpan DefaultTtl { get; } = TimeSpan.FromDays(5);

    /// <summary>The hard maximum queue item time-to-live: 5 days.</summary>
    public static TimeSpan MaxTtl { get; } = TimeSpan.FromDays(5);

    /// <summary>
    /// Returns the delay before the given attempt number, following the staged schedule:
    /// 1 minute, 5 minutes, 15 minutes, then hourly for every attempt after that.
    /// </summary>
    /// <param name="attemptCount">The 1-based number of the attempt about to be scheduled.</param>
    public static TimeSpan GetDelay(int attemptCount)
    {
        if (attemptCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptCount), attemptCount, "Attempt count must be at least 1.");
        }

        return attemptCount switch
        {
            1 => TimeSpan.FromMinutes(1),
            2 => TimeSpan.FromMinutes(5),
            3 => TimeSpan.FromMinutes(15),
            _ => TimeSpan.FromHours(1),
        };
    }

    /// <summary>Caps a configured TTL at <see cref="MaxTtl"/>.</summary>
    public static TimeSpan ValidateTtl(TimeSpan configuredTtl) =>
        configuredTtl > MaxTtl ? MaxTtl : configuredTtl;
}
