using System.ComponentModel.DataAnnotations;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// Config section for the "M365Oauth" outbound provider selection: Microsoft 365 via SMTP
/// (smtp.office365.com:587, STARTTLS) authenticated with an MSAL client-credentials OAuth token
/// (scope "https://outlook.office365.com/.default"). Only validated when this provider is the
/// one selected by <see cref="OutboundProviderOptions.Provider"/>.
/// </summary>
public sealed class M365OauthSettings
{
    [Required(AllowEmptyStrings = false)]
    public string? TenantId { get; init; }

    [Required(AllowEmptyStrings = false)]
    public string? ClientId { get; init; }

    [Required(AllowEmptyStrings = false)]
    public string? ClientSecret { get; init; }

    /// <summary>The sender mailbox; used as the SMTP AUTH username for XOAUTH2.</summary>
    [Required(AllowEmptyStrings = false)]
    public string? Mailbox { get; init; }
}
