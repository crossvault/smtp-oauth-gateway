using SmtpGateway.Core;
using Xunit;

namespace SmtpGateway.Core.Tests;

public class QueueItemExpiryPolicyTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(5);

    [Fact]
    public void IsExpired_JustBeforeTtl_ReturnsFalse()
    {
        var nowUtc = CreatedAtUtc + Ttl - TimeSpan.FromSeconds(1);

        var result = QueueItemExpiryPolicy.IsExpired(CreatedAtUtc, Ttl, nowUtc);

        Assert.False(result);
    }

    [Fact]
    public void IsExpired_ExactlyAtTtl_ReturnsTrue()
    {
        var nowUtc = CreatedAtUtc + Ttl;

        var result = QueueItemExpiryPolicy.IsExpired(CreatedAtUtc, Ttl, nowUtc);

        Assert.True(result);
    }

    [Fact]
    public void IsExpired_JustAfterTtl_ReturnsTrue()
    {
        var nowUtc = CreatedAtUtc + Ttl + TimeSpan.FromSeconds(1);

        var result = QueueItemExpiryPolicy.IsExpired(CreatedAtUtc, Ttl, nowUtc);

        Assert.True(result);
    }
}
