using SmtpGateway.Core;
using Xunit;

namespace SmtpGateway.Core.Tests;

public class RecipientDeliveryTests
{
    [Fact]
    public void Constructor_DefaultsToPendingWithNoAttemptsAndNoError()
    {
        var delivery = new RecipientDelivery("a@example.com");

        Assert.Equal("a@example.com", delivery.Address);
        Assert.Equal(RecipientStatus.Pending, delivery.Status);
        Assert.Equal(0, delivery.AttemptCount);
        Assert.Null(delivery.LastError);
    }

    [Fact]
    public void Constructor_Throws_WhenAddressIsEmpty()
    {
        Assert.Throws<ArgumentException>(() => new RecipientDelivery(""));
    }

    [Fact]
    public void WithExpression_CanUpdateStatusAttemptCountAndError()
    {
        var delivery = new RecipientDelivery("a@example.com");

        var updated = delivery with { Status = RecipientStatus.Retryable, AttemptCount = 1, LastError = "timeout" };

        Assert.Equal(RecipientStatus.Retryable, updated.Status);
        Assert.Equal(1, updated.AttemptCount);
        Assert.Equal("timeout", updated.LastError);
        // original unchanged
        Assert.Equal(RecipientStatus.Pending, delivery.Status);
    }
}
