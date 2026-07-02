namespace SmtpGateway.Infrastructure;

/// <summary>
/// The single active outbound provider type, selected via <see cref="OutboundProviderOptions.Provider"/>.
/// Exactly one provider is active at a time in the MVP - no domain/sender/fallback routing rules.
/// </summary>
public enum OutboundProviderKind
{
    /// <summary>A generic (non-Graph) SMTP relay via <see cref="GenericSmtpProvider"/>.</summary>
    GenericSmtp,

    /// <summary>Microsoft 365 via SMTP with OAuth (MSAL client-credentials) via <see cref="GenericSmtpProvider"/>.</summary>
    M365Oauth,

    /// <summary>Microsoft Graph's raw-MIME sendMail via <see cref="GraphSendMailProvider"/>.</summary>
    Graph,
}
