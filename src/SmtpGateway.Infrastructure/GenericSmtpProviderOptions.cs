using SmtpGateway.Core;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// Configuration for <see cref="GenericSmtpProvider"/>: how to connect to and authenticate with
/// a generic (non-Graph) outbound SMTP relay.
/// </summary>
public sealed class GenericSmtpProviderOptions
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    public SmtpTlsMode TlsMode { get; init; } = SmtpTlsMode.StartTlsRequired;

    public AuthMode AuthMode { get; init; } = AuthMode.None;

    public string? Username { get; init; }

    public string? Password { get; init; }

    /// <summary>
    /// Insecure legacy escape hatch: accept any server certificate, skipping hostname/trust-chain
    /// validation entirely. Default false; only for talking to relays with self-signed/expired
    /// certificates where the caller has already accepted the risk.
    /// </summary>
    public bool TrustAllCertificates { get; init; }

    /// <summary>
    /// Supplies the bearer access token for SASL XOAUTH2 authentication. Required when
    /// <see cref="AuthMode"/> is <see cref="Core.AuthMode.M365Oauth"/>; ignored otherwise.
    /// </summary>
    public ITokenProvider? TokenProvider { get; init; }
}
