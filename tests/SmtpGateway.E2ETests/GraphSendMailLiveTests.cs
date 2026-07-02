using System.Security.Cryptography;
using SmtpGateway.Core;
using SmtpGateway.Infrastructure;
using Xunit;

namespace SmtpGateway.E2ETests;

/// <summary>
/// Live tests for the Microsoft Graph single-call raw-MIME <c>sendMail</c> path against the real
/// Graph API (previously only exercised against fakes). Skips cleanly when <c>.env</c> is absent,
/// and also when the sandbox app does not hold the Graph application permission the exercised
/// product code path requires (the provider's sendMail path needs only <c>Mail.Send</c>; the
/// optional mailbox-access probe needs a directory-read permission such as <c>User.Read.All</c>).
/// </summary>
public sealed class GraphSendMailLiveTests
{
    private const string GraphScope = "https://graph.microsoft.com/.default";

    private static MsalTokenProvider BuildTokenProvider(E2ECredentials creds) =>
        new(creds.TenantId, creds.ClientId, creds.ClientSecret, GraphScope);

    [Fact]
    public async Task Submit_SendsRawMimeViaGraph_AllRecipientsSucceed()
    {
        var creds = E2ECredentials.Shared;
        Assert.SkipUnless(creds.Available, "Live O365 E2E credentials (.env) not present; skipping.");

        var ct = TestContext.Current.CancellationToken;
        var tokenProvider = BuildTokenProvider(creds);
        var roles = GraphAppRoles.Read(await tokenProvider.GetAccessTokenAsync(ct));
        Assert.SkipUnless(
            roles.Contains("Mail.Send"),
            "Sandbox app lacks the Mail.Send Graph application permission that the provider's "
            + "single-call sendMail path (POST /users/{mailbox}/sendMail) requires; skipping.");

        var provider = new GraphSendMailProvider(new GraphSendMailProviderOptions
        {
            Mailbox = creds.SenderMailbox,
            TokenProvider = tokenProvider,
        });
        var (envelope, rawMime) = LiveTestMessage.BuildRaw(creds, "graph-submit");

        var results = await provider.Submit(envelope, rawMime, ct);

        Assert.Equal(envelope.Recipients.Count, results.Count);
        Assert.All(results.Values, outcome => Assert.Equal(OutboundSubmitResult.Success, outcome.Result));
    }

    [Fact]
    public async Task Submit_SendsSingleAttachmentViaGraph_AllRecipientsSucceed()
    {
        var creds = E2ECredentials.Shared;
        Assert.SkipUnless(creds.Available, "Live O365 E2E credentials (.env) not present; skipping.");

        var ct = TestContext.Current.CancellationToken;
        var tokenProvider = BuildTokenProvider(creds);
        var roles = GraphAppRoles.Read(await tokenProvider.GetAccessTokenAsync(ct));
        Assert.SkipUnless(
            roles.Contains("Mail.Send"),
            "Sandbox app lacks the Mail.Send Graph application permission that the provider's "
            + "single-call sendMail path (POST /users/{mailbox}/sendMail) requires; skipping.");

        var provider = new GraphSendMailProvider(new GraphSendMailProviderOptions
        {
            Mailbox = creds.SenderMailbox,
            TokenProvider = tokenProvider,
        });

        // A small random binary attachment proves attachments survive the base64 sendMail path.
        var content = RandomNumberGenerator.GetBytes(4096);
        var fileName = $"graph-payload-{Guid.NewGuid():N}.bin";
        var (envelope, rawMime) = LiveTestMessage.BuildRawWithAttachment(creds, "graph-attach", fileName, content);

        var results = await provider.Submit(envelope, rawMime, ct);

        Assert.Equal(envelope.Recipients.Count, results.Count);
        Assert.All(results.Values, outcome => Assert.Equal(OutboundSubmitResult.Success, outcome.Result));
    }

    [Fact]
    public async Task TestMailboxAccess_ReadsSenderMailbox_Succeeds()
    {
        var creds = E2ECredentials.Shared;
        Assert.SkipUnless(creds.Available, "Live O365 E2E credentials (.env) not present; skipping.");

        var ct = TestContext.Current.CancellationToken;
        var tokenProvider = BuildTokenProvider(creds);
        var roles = GraphAppRoles.Read(await tokenProvider.GetAccessTokenAsync(ct));
        Assert.SkipUnless(
            roles.Contains("User.Read.All") || roles.Contains("Directory.Read.All") || roles.Contains("User.ReadBasic.All"),
            "Sandbox app lacks a Graph directory-read permission (e.g. User.Read.All) that the mailbox "
            + "check (GET /users/{id}) requires; skipping.");

        var provider = new GraphSendMailProvider(new GraphSendMailProviderOptions
        {
            Mailbox = creds.SenderMailbox,
            TokenProvider = tokenProvider,
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        var result = await provider.TestMailboxAccessAsync(timeout.Token);

        Assert.True(result.Success, result.ErrorMessage);
    }
}
