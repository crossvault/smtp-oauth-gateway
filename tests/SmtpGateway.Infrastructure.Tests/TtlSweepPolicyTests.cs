using SmtpGateway.Infrastructure;
using Xunit;

namespace SmtpGateway.Infrastructure.Tests;

public sealed class TtlSweepPolicyTests
{
    private static readonly DateTimeOffset LastSweep = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    [Fact]
    public void IsDue_ReturnsFalse_WhenElapsedIsLessThanInterval()
    {
        var now = LastSweep + TimeSpan.FromMinutes(14);

        Assert.False(TtlSweepPolicy.IsDue(LastSweep, now, Interval));
    }

    [Fact]
    public void IsDue_ReturnsTrue_WhenElapsedExactlyEqualsInterval()
    {
        var now = LastSweep + Interval;

        Assert.True(TtlSweepPolicy.IsDue(LastSweep, now, Interval));
    }

    [Fact]
    public void IsDue_ReturnsTrue_WhenElapsedExceedsInterval()
    {
        var now = LastSweep + TimeSpan.FromMinutes(16);

        Assert.True(TtlSweepPolicy.IsDue(LastSweep, now, Interval));
    }

    [Fact]
    public void IsDue_ReturnsFalse_WhenNowIsBeforeLastSweep()
    {
        var now = LastSweep - TimeSpan.FromMinutes(1);

        Assert.False(TtlSweepPolicy.IsDue(LastSweep, now, Interval));
    }
}
