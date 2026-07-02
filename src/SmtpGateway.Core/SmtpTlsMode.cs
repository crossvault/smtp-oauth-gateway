namespace SmtpGateway.Core;

/// <summary>
/// The TLS negotiation mode for an outbound SMTP connection. Cross-cutting provider concept,
/// hence defined in Core even though the configured default (StartTlsRequired) lives in
/// Infrastructure.
/// </summary>
public enum SmtpTlsMode
{
    /// <summary>Require STARTTLS on the plaintext connection before authenticating/sending.</summary>
    StartTlsRequired,

    /// <summary>Negotiate TLS immediately on connect (implicit TLS / SMTPS).</summary>
    SslOnConnect,

    /// <summary>No TLS at all.</summary>
    None,
}
