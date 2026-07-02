using SmtpGateway.Infrastructure;
using Xunit;

namespace SmtpGateway.E2ETests;

/// <summary>
/// Live sanity check that <see cref="MsalTokenProvider"/> acquires a real client-credentials
/// token and serves the cached value on a second call. Skips cleanly when <c>.env</c> is absent.
/// </summary>
public sealed class TokenProviderLiveTests
{
    private const string SmtpScope = "https://outlook.office365.com/.default";

    [Fact]
    public async Task GetAccessTokenAsync_ReturnsToken_AndCachesIt()
    {
        var creds = E2ECredentials.Shared;
        Assert.SkipUnless(creds.Available, "Live O365 E2E credentials (.env) not present; skipping.");

        var tokenProvider = new MsalTokenProvider(creds.TenantId, creds.ClientId, creds.ClientSecret, SmtpScope);
        var ct = TestContext.Current.CancellationToken;

        var first = await tokenProvider.GetAccessTokenAsync(ct);
        var second = await tokenProvider.GetAccessTokenAsync(ct);

        // Deliberately assert without ever placing the token value into the assertion message.
        Assert.True(first.Length > 0, "Expected a non-empty access token from the first acquisition.");
        Assert.True(
            string.Equals(first, second, StringComparison.Ordinal),
            "Expected the second acquisition to return the cached token (no second MSAL roundtrip).");
    }
}
