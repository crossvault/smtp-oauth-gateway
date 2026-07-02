using System.ComponentModel.DataAnnotations;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// Config-friendly (appsettings.json-bindable) representation of <see cref="SmtpGatewayOptions"/>.
/// <see cref="BindEndpoints"/> holds plain strings (e.g. "127.0.0.1:2525", "[::1]:2525") since an
/// <see cref="System.Net.IPEndPoint"/> has no natural JSON representation; use
/// <see cref="ToSmtpGatewayOptions"/> to map to the real type consumed by
/// <see cref="SmtpGatewayListener"/>.
/// </summary>
public sealed class SmtpInboundOptions
{
    /// <summary>
    /// Loopback bind endpoints, e.g. "127.0.0.1:2525" or "[::1]:2525". Must be configured
    /// explicitly (empty by default, not "127.0.0.1:2525") - Microsoft.Extensions.Configuration's
    /// binder APPENDS configured array values to an existing non-empty default instead of
    /// replacing it (see SmtpInboundOptionsBindingTests), so a non-empty compile-time default here
    /// would silently keep listening on it alongside whatever appsettings.json configures.
    /// </summary>
    [MinLength(1)]
    public IReadOnlyList<string> BindEndpoints { get; init; } = [];

    /// <summary>
    /// When false (the default), any non-loopback bind endpoint (a LAN IP or a wildcard "0.0.0.0" /
    /// "[::]") fails fast at startup. Set true to deliberately permit a network-reachable bind - see
    /// the startup warnings emitted by the service in that case.
    /// </summary>
    public bool AllowNonLoopbackBind { get; init; }

    /// <summary>
    /// Optional inbound SMTP AUTH username. When both this and <see cref="AuthPassword"/> are set,
    /// AUTH is enabled AND required for every inbound session (loopback included); leaving both
    /// null/empty disables inbound AUTH. Configuring exactly one is a startup configuration error
    /// (see <see cref="GatewayOptionsValidator"/>).
    /// </summary>
    public string? AuthUsername { get; init; }

    /// <summary>Optional inbound SMTP AUTH password; see <see cref="AuthUsername"/>. Never logged.</summary>
    public string? AuthPassword { get; init; }

    public string ServerName { get; init; } = "smtpoauth";

    [Range(1, int.MaxValue)]
    public int MaxMessageSizeBytes { get; init; } = SmtpGatewayOptions.DefaultMaxMessageSizeBytes;

    [Range(1, int.MaxValue)]
    public int MaxRecipients { get; init; } = SmtpGatewayOptions.DefaultMaxRecipients;

    public TimeSpan IdleTimeout { get; init; } = SmtpGatewayOptions.DefaultIdleTimeout;

    /// <summary>Maps to the real <see cref="SmtpGatewayOptions"/>, parsing <see cref="BindEndpoints"/> via <see cref="SmtpBindEndpointParser"/>.</summary>
    public SmtpGatewayOptions ToSmtpGatewayOptions() => new()
    {
        BindEndpoints = SmtpBindEndpointParser.ParseAll(BindEndpoints),
        AllowNonLoopbackBind = AllowNonLoopbackBind,
        AuthUsername = AuthUsername,
        AuthPassword = AuthPassword,
        ServerName = ServerName,
        MaxMessageSizeBytes = MaxMessageSizeBytes,
        MaxRecipients = MaxRecipients,
        IdleTimeout = IdleTimeout,
    };
}
