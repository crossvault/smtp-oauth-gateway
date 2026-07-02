using System.ComponentModel.DataAnnotations;
using SmtpGateway.Core;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// Config section for the "GenericSmtp" outbound provider selection: connects to any generic
/// SMTP relay via <see cref="GenericSmtpProvider"/>. Only validated when this provider is the
/// one selected by <see cref="OutboundProviderOptions.Provider"/>.
/// </summary>
public sealed class GenericSmtpSettings
{
    [Required(AllowEmptyStrings = false)]
    public string? Host { get; init; }

    [Range(1, 65535)]
    public int Port { get; init; } = 587;

    public SmtpTlsMode TlsMode { get; init; } = SmtpTlsMode.StartTlsRequired;

    public AuthMode AuthMode { get; init; } = AuthMode.None;

    public string? Username { get; init; }

    public string? Password { get; init; }

    public bool TrustAllCertificates { get; init; }
}
