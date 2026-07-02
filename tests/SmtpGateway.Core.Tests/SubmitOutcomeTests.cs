using SmtpGateway.Core;
using Xunit;

namespace SmtpGateway.Core.Tests;

public class SubmitOutcomeTests
{
    [Fact]
    public void Constructor_RetryAfterOmitted_DefaultsToNull()
    {
        var outcome = new SubmitOutcome(OutboundSubmitResult.Success);

        Assert.Equal(OutboundSubmitResult.Success, outcome.Result);
        Assert.Null(outcome.RetryAfter);
    }

    [Fact]
    public void Constructor_RetryAfterProvided_IsRetained()
    {
        var retryAfter = TimeSpan.FromSeconds(30);

        var outcome = new SubmitOutcome(OutboundSubmitResult.RetryableFailure, retryAfter);

        Assert.Equal(OutboundSubmitResult.RetryableFailure, outcome.Result);
        Assert.Equal(retryAfter, outcome.RetryAfter);
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = new SubmitOutcome(OutboundSubmitResult.PermanentFailure, TimeSpan.FromMinutes(1));
        var b = new SubmitOutcome(OutboundSubmitResult.PermanentFailure, TimeSpan.FromMinutes(1));

        Assert.Equal(a, b);
    }
}
