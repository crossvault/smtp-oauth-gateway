using SmtpGateway.Core;
using Xunit;

namespace SmtpGateway.Core.Tests;

public class EnvelopeTests
{
    [Fact]
    public void Constructor_SetsMailFromAndRecipients()
    {
        var envelope = new Envelope("sender@example.com", ["a@example.com", "b@example.com"]);

        Assert.Equal("sender@example.com", envelope.MailFrom);
        Assert.Equal(["a@example.com", "b@example.com"], envelope.Recipients);
    }

    [Fact]
    public void Constructor_Throws_WhenMailFromIsEmpty()
    {
        Assert.Throws<ArgumentException>(() => new Envelope("", ["a@example.com"]));
    }

    [Fact]
    public void Constructor_Throws_WhenRecipientsIsEmpty()
    {
        Assert.Throws<ArgumentException>(() => new Envelope("sender@example.com", []));
    }

    [Fact]
    public void Recipients_IsReadOnly()
    {
        var envelope = new Envelope("sender@example.com", ["a@example.com"]);

        Assert.IsAssignableFrom<IReadOnlyCollection<string>>(envelope.Recipients);
    }
}
