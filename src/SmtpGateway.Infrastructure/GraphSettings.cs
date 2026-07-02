using System.ComponentModel.DataAnnotations;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// Config section for the "Graph" outbound provider selection: Microsoft Graph's raw-MIME
/// sendMail via <see cref="GraphSendMailProvider"/>, authenticated with an MSAL
/// client-credentials OAuth token (scope "https://graph.microsoft.com/.default"). Only validated
/// when this provider is the one selected by <see cref="OutboundProviderOptions.Provider"/>.
/// </summary>
public sealed class GraphSettings
{
    [Required(AllowEmptyStrings = false)]
    public string? TenantId { get; init; }

    [Required(AllowEmptyStrings = false)]
    public string? ClientId { get; init; }

    [Required(AllowEmptyStrings = false)]
    public string? ClientSecret { get; init; }

    /// <summary>The sender mailbox (e.g. "gateway@tenant.onmicrosoft.com").</summary>
    [Required(AllowEmptyStrings = false)]
    public string? Mailbox { get; init; }
}
