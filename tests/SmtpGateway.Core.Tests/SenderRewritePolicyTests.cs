using SmtpGateway.Core;
using Xunit;

namespace SmtpGateway.Core.Tests;

public class SenderRewritePolicyTests
{
    [Fact]
    public void Resolve_NoRewriteConfigured_ReturnsOriginal()
    {
        var result = SenderRewritePolicy.Resolve("original@example.com", null);

        Assert.Equal("original@example.com", result);
    }

    [Fact]
    public void Resolve_RewriteConfigured_ReturnsRewriteAddress()
    {
        var result = SenderRewritePolicy.Resolve("original@example.com", "rewritten@example.com");

        Assert.Equal("rewritten@example.com", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_RewriteAddressEmptyOrWhitespace_TreatedAsNotConfigured(string rewriteAddress)
    {
        var result = SenderRewritePolicy.Resolve("original@example.com", rewriteAddress);

        Assert.Equal("original@example.com", result);
    }
}
