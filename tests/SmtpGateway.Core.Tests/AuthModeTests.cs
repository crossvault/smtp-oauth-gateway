using SmtpGateway.Core;
using Xunit;

namespace SmtpGateway.Core.Tests;

public class AuthModeTests
{
    [Fact]
    public void HasExactlyThreeBuiltInValues()
    {
        var values = Enum.GetValues<AuthMode>();

        Assert.Equal(3, values.Length);
        Assert.Contains(AuthMode.None, values);
        Assert.Contains(AuthMode.UsernamePassword, values);
        Assert.Contains(AuthMode.M365Oauth, values);
    }
}
