using System.Net;
using SmtpGateway.Infrastructure;
using Xunit;

namespace SmtpGateway.Infrastructure.Tests;

public sealed class LoopbackEndpointValidatorTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.5")]
    [InlineData("::1")]
    public void ValidateLoopbackOnly_AcceptsLoopbackAddresses(string address)
    {
        var endpoints = new[] { new IPEndPoint(IPAddress.Parse(address), 2525) };

        var exception = Record.Exception(() => LoopbackEndpointValidator.ValidateLoopbackOnly(endpoints));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("192.168.1.10")]
    [InlineData("10.0.0.5")]
    public void ValidateLoopbackOnly_ThrowsForNonLoopbackAddresses(string address)
    {
        var endpoints = new[] { new IPEndPoint(IPAddress.Parse(address), 2525) };

        Assert.Throws<InvalidOperationException>(() => LoopbackEndpointValidator.ValidateLoopbackOnly(endpoints));
    }

    [Fact]
    public void ValidateLoopbackOnly_ThrowsWhenAnyEndpointInAMixedListIsNonLoopback()
    {
        var endpoints = new[]
        {
            new IPEndPoint(IPAddress.Loopback, 2525),
            new IPEndPoint(IPAddress.Any, 2525),
        };

        Assert.Throws<InvalidOperationException>(() => LoopbackEndpointValidator.ValidateLoopbackOnly(endpoints));
    }

    [Fact]
    public void ValidateLoopbackOnly_AllowsEmptyList()
    {
        var exception = Record.Exception(() => LoopbackEndpointValidator.ValidateLoopbackOnly(Array.Empty<IPEndPoint>()));

        Assert.Null(exception);
    }
}
