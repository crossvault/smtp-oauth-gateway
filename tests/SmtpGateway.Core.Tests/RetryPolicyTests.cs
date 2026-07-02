using SmtpGateway.Core;
using Xunit;

namespace SmtpGateway.Core.Tests;

public class RetryPolicyTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    public void GetDelay_ReturnsStagedMinutes(int attemptCount, int expectedMinutes)
    {
        var delay = RetryPolicy.GetDelay(attemptCount);

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), delay);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(10)]
    public void GetDelay_StaysHourly_AfterThirdAttempt(int attemptCount)
    {
        var delay = RetryPolicy.GetDelay(attemptCount);

        Assert.Equal(TimeSpan.FromHours(1), delay);
    }

    [Fact]
    public void GetDelay_Throws_WhenAttemptCountIsNotPositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.GetDelay(0));
    }

    [Fact]
    public void DefaultTtl_IsFiveDays()
    {
        Assert.Equal(TimeSpan.FromDays(5), RetryPolicy.DefaultTtl);
    }

    [Fact]
    public void MaxTtl_IsFiveDays()
    {
        Assert.Equal(TimeSpan.FromDays(5), RetryPolicy.MaxTtl);
    }

    [Fact]
    public void ValidateTtl_ReturnsConfiguredValue_WhenWithinMax()
    {
        var configured = TimeSpan.FromDays(2);

        var validated = RetryPolicy.ValidateTtl(configured);

        Assert.Equal(configured, validated);
    }

    [Fact]
    public void ValidateTtl_CapsAtMax_WhenConfiguredExceedsMax()
    {
        var configured = TimeSpan.FromDays(30);

        var validated = RetryPolicy.ValidateTtl(configured);

        Assert.Equal(TimeSpan.FromDays(5), validated);
    }
}
