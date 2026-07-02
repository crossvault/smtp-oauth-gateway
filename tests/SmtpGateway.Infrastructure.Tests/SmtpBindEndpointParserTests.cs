using System.Net;
using SmtpGateway.Infrastructure;
using Xunit;

namespace SmtpGateway.Infrastructure.Tests;

public sealed class SmtpBindEndpointParserTests
{
    [Fact]
    public void Parse_AcceptsIPv4WithPort()
    {
        var endpoint = SmtpBindEndpointParser.Parse("127.0.0.1:2525");

        Assert.Equal(IPAddress.Parse("127.0.0.1"), endpoint.Address);
        Assert.Equal(2525, endpoint.Port);
    }

    [Fact]
    public void Parse_AcceptsBracketedIPv6WithPort()
    {
        var endpoint = SmtpBindEndpointParser.Parse("[::1]:2525");

        Assert.Equal(IPAddress.Parse("::1"), endpoint.Address);
        Assert.Equal(2525, endpoint.Port);
    }

    [Fact]
    public void Parse_AcceptsIPv4WildcardWithPort()
    {
        var endpoint = SmtpBindEndpointParser.Parse("0.0.0.0:2525");

        Assert.Equal(IPAddress.Any, endpoint.Address);
        Assert.Equal(2525, endpoint.Port);
    }

    [Fact]
    public void Parse_AcceptsBracketedIPv6WildcardWithPort()
    {
        var endpoint = SmtpBindEndpointParser.Parse("[::]:2525");

        Assert.Equal(IPAddress.IPv6Any, endpoint.Address);
        Assert.Equal(2525, endpoint.Port);
    }

    [Theory]
    [InlineData("not-an-endpoint")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:notaport")]
    [InlineData("127.0.0.1:99999")]
    [InlineData("::1:2525")]
    public void Parse_ThrowsForMalformedStrings(string value)
    {
        Assert.Throws<FormatException>(() => SmtpBindEndpointParser.Parse(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_ThrowsForNullOrWhitespace(string? value)
    {
        Assert.ThrowsAny<ArgumentException>(() => SmtpBindEndpointParser.Parse(value!));
    }

    [Fact]
    public void ParseAll_MapsEveryEntryInOrder()
    {
        var endpoints = SmtpBindEndpointParser.ParseAll(["127.0.0.1:2525", "[::1]:2526"]);

        Assert.Equal(2, endpoints.Count);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 2525), endpoints[0]);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("::1"), 2526), endpoints[1]);
    }

    [Fact]
    public void ParseAll_AllowsEmptyList()
    {
        var endpoints = SmtpBindEndpointParser.ParseAll([]);

        Assert.Empty(endpoints);
    }

    [Fact]
    public void ParseAll_ThrowsIfAnyEntryIsMalformed()
    {
        Assert.Throws<FormatException>(() => SmtpBindEndpointParser.ParseAll(["127.0.0.1:2525", "garbage"]));
    }
}
