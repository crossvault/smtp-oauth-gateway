using SmtpGateway.Core;
using SmtpGateway.Infrastructure;
using Xunit;

namespace SmtpGateway.E2ETests;

/// <summary>
/// Live tests for the M365 SMTP OAuth (XOAUTH2) path against smtp.office365.com using a real
/// client-credentials token. Skips cleanly when <c>.env</c> is absent.
/// </summary>
public sealed class M365SmtpOAuthLiveTests
{
    private const string SmtpScope = "https://outlook.office365.com/.default";

    private static GenericSmtpProvider BuildProvider(E2ECredentials creds)
    {
        var tokenProvider = new MsalTokenProvider(creds.TenantId, creds.ClientId, creds.ClientSecret, SmtpScope);
        return new GenericSmtpProvider(new GenericSmtpProviderOptions
        {
            Host = "smtp.office365.com",
            Port = 587,
            TlsMode = SmtpTlsMode.StartTlsRequired,
            AuthMode = AuthMode.M365Oauth,
            Username = creds.SenderMailbox,
            TokenProvider = tokenProvider,
        });
    }

    [Fact]
    public async Task Submit_RelaysMessage_AllRecipientsSucceed()
    {
        var creds = E2ECredentials.Shared;
        Assert.SkipUnless(creds.Available, "Live O365 E2E credentials (.env) not present; skipping.");

        var provider = BuildProvider(creds);
        var (envelope, rawMime) = LiveTestMessage.BuildRaw(creds, "smtp-submit");

        var results = await provider.Submit(envelope, rawMime, TestContext.Current.CancellationToken);

        Assert.Equal(envelope.Recipients.Count, results.Count);
        Assert.All(results.Values, outcome => Assert.Equal(OutboundSubmitResult.Success, outcome.Result));
    }

    [Fact]
    public async Task TestConnection_ConnectsTlsAndAuthenticates_Succeeds()
    {
        var creds = E2ECredentials.Shared;
        Assert.SkipUnless(creds.Available, "Live O365 E2E credentials (.env) not present; skipping.");

        var provider = BuildProvider(creds);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        var result = await provider.TestConnectionAsync(timeout.Token);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Null(result.ErrorMessage);
    }
}
