using SmtpGateway.Infrastructure;
using Xunit;

namespace SmtpGateway.Infrastructure.Tests;

public sealed class MsalTokenAcquirerTests
{
    [Fact]
    public void Constructor_WithFakeLookingConfig_DoesNotThrow()
    {
        // Smoke test only: building a ConfidentialClientApplication does not make any network
        // call, so fake-looking values are safe here. Acquiring a real token requires live
        // Entra credentials and is deliberately never exercised in tests.
        var acquirer = new MsalTokenAcquirer(
            tenantId: "00000000-0000-0000-0000-000000000000",
            clientId: "11111111-1111-1111-1111-111111111111",
            clientSecret: "fake-secret",
            scope: "https://outlook.office365.com/.default");

        Assert.NotNull(acquirer);
    }
}
