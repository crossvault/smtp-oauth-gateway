using System.ComponentModel.DataAnnotations;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// Discriminated outbound-provider configuration: <see cref="Provider"/> selects exactly one of
/// "GenericSmtp", "M365Oauth", or "Graph" (case-insensitive, matched against
/// <see cref="OutboundProviderKind"/>) as the active provider. Only the selected provider's
/// settings section needs to be populated/valid - see <see cref="OutboundProviderFactory"/>.
/// </summary>
public sealed class OutboundProviderOptions
{
    [Required(AllowEmptyStrings = false)]
    public string Provider { get; init; } = string.Empty;

    public GenericSmtpSettings? GenericSmtp { get; init; }

    public M365OauthSettings? M365Oauth { get; init; }

    public GraphSettings? Graph { get; init; }
}
