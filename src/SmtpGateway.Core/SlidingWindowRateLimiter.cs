namespace SmtpGateway.Core;

/// <summary>
/// Pure sliding-window rate limiter: allows at most <c>maxPerWindow</c> acquisitions within any
/// rolling <c>window</c> of time ending at "now". Has no timer, no thread, and never reads the
/// wall clock or sleeps itself - the caller passes "now" into <see cref="TryAcquire"/> explicitly,
/// so tests can simulate a full window rolling over just by advancing that value, with no real
/// delay. Used to optionally cap how fast the outbound delivery loop submits to a configured
/// outbound provider.
/// </summary>
/// <remarks>
/// ponytail: not thread-safe. The only caller is the single-threaded outbound delivery loop, so a
/// lock would be pure speculative overhead for a scenario that cannot happen.
/// </remarks>
public sealed class SlidingWindowRateLimiter
{
    private readonly int _maxPerWindow;
    private readonly TimeSpan _window;
    private readonly Queue<DateTimeOffset> _acquisitions = new();

    public SlidingWindowRateLimiter(int maxPerWindow, TimeSpan window)
    {
        if (maxPerWindow <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPerWindow), maxPerWindow, "Must be at least 1.");
        }

        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), window, "Must be a positive duration.");
        }

        _maxPerWindow = maxPerWindow;
        _window = window;
    }

    /// <summary>
    /// Attempts to record one acquisition at <paramref name="nowUtc"/>. First discards any
    /// previously recorded acquisitions that have fallen outside the rolling window, then returns
    /// <c>true</c> (and records this acquisition) only if fewer than <c>maxPerWindow</c> remain;
    /// otherwise returns <c>false</c> and records nothing.
    /// </summary>
    public bool TryAcquire(DateTimeOffset nowUtc)
    {
        while (_acquisitions.Count > 0 && nowUtc - _acquisitions.Peek() >= _window)
        {
            _acquisitions.Dequeue();
        }

        if (_acquisitions.Count >= _maxPerWindow)
        {
            return false;
        }

        _acquisitions.Enqueue(nowUtc);
        return true;
    }
}
