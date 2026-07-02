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
        ServerName = ServerName,
        MaxMessageSizeBytes = MaxMessageSizeBytes,
        MaxRecipients = MaxRecipients,
        IdleTimeout = IdleTimeout,
    };
}
