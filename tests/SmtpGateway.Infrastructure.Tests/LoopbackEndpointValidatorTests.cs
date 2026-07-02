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
    public void Validate_LoopbackWithFlagOff_IsAccepted(string address)
    {
        var endpoints = new[] { new IPEndPoint(IPAddress.Parse(address), 2525) };

        var exception = Record.Exception(() => LoopbackEndpointValidator.Validate(endpoints, allowNonLoopback: false));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("192.168.1.10")]
    [InlineData("10.0.0.5")]
    public void Validate_LanWithFlagOff_ThrowsMentioningTheFlag(string address)
    {
        var endpoints = new[] { new IPEndPoint(IPAddress.Parse(address), 2525) };

        var exception = Assert.Throws<InvalidOperationException>(
            () => LoopbackEndpointValidator.Validate(endpoints, allowNonLoopback: false));

        Assert.Contains("Smtp:AllowNonLoopbackBind", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("192.168.1.10")]
    [InlineData("10.0.0.5")]
    public void Validate_LanWithFlagOn_IsAccepted(string address)
    {
        var endpoints = new[] { new IPEndPoint(IPAddress.Parse(address), 2525) };

        var exception = Record.Exception(() => LoopbackEndpointValidator.Validate(endpoints, allowNonLoopback: true));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void Validate_WildcardWithFlagOn_IsAccepted(string address)
    {
        var endpoints = new[] { new IPEndPoint(IPAddress.Parse(address), 2525) };

        var exception = Record.Exception(() => LoopbackEndpointValidator.Validate(endpoints, allowNonLoopback: true));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void Validate_WildcardWithFlagOff_Throws(string address)
    {
        var endpoints = new[] { new IPEndPoint(IPAddress.Parse(address), 2525) };

        var exception = Assert.Throws<InvalidOperationException>(
            () => LoopbackEndpointValidator.Validate(endpoints, allowNonLoopback: false));

        Assert.Contains("Smtp:AllowNonLoopbackBind", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_MixedListWithOneNonLoopbackAndFlagOff_Throws()
    {
        var endpoints = new[]
        {
            new IPEndPoint(IPAddress.Loopback, 2525),
            new IPEndPoint(IPAddress.Any, 2525),
        };

        Assert.Throws<InvalidOperationException>(
            () => LoopbackEndpointValidator.Validate(endpoints, allowNonLoopback: false));
    }

    [Fact]
    public void Validate_EmptyList_IsAcceptedRegardlessOfFlag()
    {
        Assert.Null(Record.Exception(() => LoopbackEndpointValidator.Validate([], allowNonLoopback: false)));
        Assert.Null(Record.Exception(() => LoopbackEndpointValidator.Validate([], allowNonLoopback: true)));
    }

    [Fact]
    public void GetNonLoopbackEndpoints_ReturnsOnlyNonLoopback_IncludingWildcards()
    {
        var loopback4 = new IPEndPoint(IPAddress.Loopback, 2525);
        var loopback6 = new IPEndPoint(IPAddress.IPv6Loopback, 2525);
        var wildcard = new IPEndPoint(IPAddress.Any, 2525);
        var lan = new IPEndPoint(IPAddress.Parse("192.168.1.10"), 2525);

        var nonLoopback = LoopbackEndpointValidator.GetNonLoopbackEndpoints(
            [loopback4, loopback6, wildcard, lan]);

        Assert.Equal([wildcard, lan], nonLoopback);
    }
}
