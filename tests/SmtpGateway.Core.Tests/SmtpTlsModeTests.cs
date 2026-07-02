using SmtpGateway.Core;
using Xunit;

namespace SmtpGateway.Core.Tests;

public class SmtpTlsModeTests
{
    [Fact]
    public void HasExactlyThreeValues()
    {
        var values = Enum.GetValues<SmtpTlsMode>();

        Assert.Equal(3, values.Length);
        Assert.Contains(SmtpTlsMode.StartTlsRequired, values);
        Assert.Contains(SmtpTlsMode.SslOnConnect, values);
        Assert.Contains(SmtpTlsMode.None, values);
    }
}
