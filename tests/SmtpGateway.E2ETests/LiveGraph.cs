using SmtpGateway.Infrastructure;
using Xunit;

namespace SmtpGateway.E2ETests;

/// <summary>
/// Shared wiring for the Graph-scope side of the live tests: builds a Graph
/// <see cref="MsalTokenProvider"/> exactly the way the product's Graph provider does, and gates the
/// mailbox-verification assertions on the live token actually carrying the <c>Mail.Read</c>
/// application permission. When that role is absent (e.g. admin consent still propagating), the
/// dependent test SKIPS with a clear reason rather than failing - the reader would otherwise get a
/// 403 from Graph that says nothing useful. The token is inspected only in-memory via
/// <see cref="GraphAppRoles"/> and never logged or placed into any message.
/// </summary>
internal static class LiveGraph
{
    private const string GraphScope = "https://graph.microsoft.com/.default";

    public const string MailReadSkip =
        "Sandbox app lacks the Mail.Read Graph application permission that the delivered-mail "
        + "verification (GET /users/{recipient}/messages + /$value) requires; admin consent may still "
        + "be propagating. Skipping the received-mailbox assertions.";

    public static MsalTokenProvider CreateGraphTokenProvider(E2ECredentials creds) =>
        new(creds.TenantId, creds.ClientId, creds.ClientSecret, GraphScope);

    /// <summary>
    /// Acquires a Graph token, inspects its roles, and returns a ready <see cref="GraphMailboxReader"/>
    /// when <c>Mail.Read</c> is present; otherwise skips the calling test with <see cref="MailReadSkip"/>.
    /// The same token provider is reused by the returned reader, so this costs a single acquisition.
    /// </summary>
    public static async Task<GraphMailboxReader> CreateMailboxReaderOrSkipAsync(
        E2ECredentials creds, CancellationToken ct)
    {
        var tokenProvider = CreateGraphTokenProvider(creds);
        var roles = GraphAppRoles.Read(await tokenProvider.GetAccessTokenAsync(ct));
        Assert.SkipUnless(roles.Contains("Mail.Read"), MailReadSkip);
        return new GraphMailboxReader(tokenProvider);
    }
}
