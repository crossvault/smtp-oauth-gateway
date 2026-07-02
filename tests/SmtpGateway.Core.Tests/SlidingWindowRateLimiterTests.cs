using SmtpGateway.Core;
using Xunit;

namespace SmtpGateway.Core.Tests;

public sealed class SlidingWindowRateLimiterTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    [Fact]
    public void TryAcquire_UnderLimit_AlwaysAllows()
    {
        var limiter = new SlidingWindowRateLimiter(maxPerWindow: 100, OneMinute);

        for (var i = 0; i < 10; i++)
        {
            Assert.True(limiter.TryAcquire(Start + TimeSpan.FromSeconds(i)));
        }
    }

    [Fact]
    public void TryAcquire_FourthCallWithinSameWindow_IsDeniedWhenLimitIsThree()
    {
        var limiter = new SlidingWindowRateLimiter(maxPerWindow: 3, OneMinute);

        Assert.True(limiter.TryAcquire(Start));
        Assert.True(limiter.TryAcquire(Start + TimeSpan.FromSeconds(1)));
        Assert.True(limiter.TryAcquire(Start + TimeSpan.FromSeconds(2)));
        Assert.False(limiter.TryAcquire(Start + TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void TryAcquire_DeniedAcquisition_DoesNotConsumeASlot()
    {
        var limiter = new SlidingWindowRateLimiter(maxPerWindow: 1, OneMinute);

        Assert.True(limiter.TryAcquire(Start));
        Assert.False(limiter.TryAcquire(Start + TimeSpan.FromSeconds(1)));
        Assert.False(limiter.TryAcquire(Start + TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void TryAcquire_AfterWindowRollsOver_AllowsAgain()
    {
        var limiter = new SlidingWindowRateLimiter(maxPerWindow: 3, OneMinute);

        Assert.True(limiter.TryAcquire(Start));
        Assert.True(limiter.TryAcquire(Start + TimeSpan.FromSeconds(1)));
        Assert.True(limiter.TryAcquire(Start + TimeSpan.FromSeconds(2)));
        Assert.False(limiter.TryAcquire(Start + TimeSpan.FromSeconds(3)));

        // Advance simulated time a full minute past the *first* acquisition - no real sleep - so
        // it (and it alone) falls outside the rolling window and a single new slot opens up.
        var justAfterFirstExpires = Start + OneMinute + TimeSpan.FromMilliseconds(1);
        Assert.True(limiter.TryAcquire(justAfterFirstExpires));
        Assert.False(limiter.TryAcquire(justAfterFirstExpires + TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void TryAcquire_ExactlyAtWindowBoundary_TreatsOldAcquisitionAsExpired()
    {
        var limiter = new SlidingWindowRateLimiter(maxPerWindow: 1, OneMinute);

        Assert.True(limiter.TryAcquire(Start));
        Assert.True(limiter.TryAcquire(Start + OneMinute));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveMaxPerWindow_Throws(int maxPerWindow)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowRateLimiter(maxPerWindow, OneMinute));
    }

    [Fact]
    public void Constructor_NonPositiveWindow_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowRateLimiter(1, TimeSpan.Zero));
    }
}
