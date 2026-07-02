using SmtpGateway.Infrastructure;
using Xunit;

namespace SmtpGateway.Infrastructure.Tests;

public sealed class OutboundProviderFactoryTests
{
    [Fact]
    public void Create_GenericSmtp_ReturnsGenericSmtpProvider()
    {
        var options = new OutboundProviderOptions
        {
            Provider = "GenericSmtp",
            GenericSmtp = new GenericSmtpSettings { Host = "relay.example.com", Port = 587 },
        };

        var provider = OutboundProviderFactory.Create(options);

        Assert.IsType<GenericSmtpProvider>(provider);
    }

    [Theory]
    [InlineData("genericsmtp")]
    [InlineData("GENERICSMTP")]
    public void Create_ProviderNameMatchIsCaseInsensitive(string provider)
    {
        var options = new OutboundProviderOptions
        {
            Provider = provider,
            GenericSmtp = new GenericSmtpSettings { Host = "relay.example.com", Port = 587 },
        };

        var result = OutboundProviderFactory.Create(options);

        Assert.IsType<GenericSmtpProvider>(result);
    }

    [Fact]
    public void Create_M365Oauth_ReturnsGenericSmtpProvider()
    {
        var options = new OutboundProviderOptions
        {
            Provider = "M365Oauth",
            M365Oauth = new M365OauthSettings
            {
                TenantId = "tenant",
                ClientId = "client",
                ClientSecret = "secret",
                Mailbox = "gateway@example.com",
            },
        };

        var provider = OutboundProviderFactory.Create(options);

        Assert.IsType<GenericSmtpProvider>(provider);
    }

    [Fact]
    public void Create_Graph_ReturnsGraphSendMailProvider()
    {
        var options = new OutboundProviderOptions
        {
            Provider = "Graph",
            Graph = new GraphSettings
            {
                TenantId = "tenant",
                ClientId = "client",
                ClientSecret = "secret",
                Mailbox = "gateway@example.com",
            },
        };

        var provider = OutboundProviderFactory.Create(options);

        Assert.IsType<GraphSendMailProvider>(provider);
    }

    [Fact]
    public void Create_ThrowsWhenSelectedSectionIsMissing()
    {
        var options = new OutboundProviderOptions { Provider = "GenericSmtp", GenericSmtp = null };

        var exception = Assert.Throws<InvalidOperationException>(() => OutboundProviderFactory.Create(options));

        Assert.Contains("GenericSmtp", exception.Message);
    }

    [Fact]
    public void Create_ThrowsWhenSelectedSectionIsIncomplete()
    {
        var options = new OutboundProviderOptions
        {
            Provider = "M365Oauth",
            M365Oauth = new M365OauthSettings { TenantId = "tenant" }, // ClientId/ClientSecret/Mailbox missing
        };

        var exception = Assert.Throws<InvalidOperationException>(() => OutboundProviderFactory.Create(options));

        Assert.Contains("ClientId", exception.Message);
        Assert.Contains("ClientSecret", exception.Message);
        Assert.Contains("Mailbox", exception.Message);
    }

    [Fact]
    public void Create_ThrowsForUnknownProviderName()
    {
        var options = new OutboundProviderOptions { Provider = "SomethingElse" };

        var exception = Assert.Throws<InvalidOperationException>(() => OutboundProviderFactory.Create(options));

        Assert.Contains("SomethingElse", exception.Message);
        Assert.Contains("GenericSmtp", exception.Message);
        Assert.Contains("M365Oauth", exception.Message);
        Assert.Contains("Graph", exception.Message);
    }
}
