using Microsoft.Identity.Client;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// Real <see cref="ITokenAcquirer"/> implementation backed by MSAL's client-credentials flow
/// against Microsoft Entra ID. Not SMTP-specific: the scope (e.g.
/// "https://outlook.office365.com/.default" for M365 SMTP AUTH) is supplied by the caller.
/// </summary>
internal sealed class MsalTokenAcquirer : ITokenAcquirer
{
    private readonly IConfidentialClientApplication _app;
    private readonly string _scope;

    public MsalTokenAcquirer(string tenantId, string clientId, string clientSecret, string scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(clientId);
        ArgumentException.ThrowIfNullOrEmpty(clientSecret);
        ArgumentException.ThrowIfNullOrEmpty(scope);

        _scope = scope;
        _app = ConfidentialClientApplicationBuilder.Create(clientId)
            .WithClientSecret(clientSecret)
            .WithTenantId(tenantId)
            .Build();
    }

    public async Task<AcquiredToken> AcquireAsync(CancellationToken ct)
    {
        var result = await _app.AcquireTokenForClient([_scope]).ExecuteAsync(ct).ConfigureAwait(false);
        return new AcquiredToken(result.AccessToken, result.ExpiresOn);
    }
}
