using System.Net;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// Configuration for the local, loopback-only inbound SMTP listener.
/// </summary>
public sealed class SmtpGatewayOptions
{
    public const int DefaultMaxMessageSizeBytes = 25 * 1024 * 1024;
    public const int DefaultMaxRecipients = 100;

    public static TimeSpan DefaultIdleTimeout { get; } = TimeSpan.FromSeconds(60);

    /// <summary>Addresses to bind. Loopback-only by default; validated by <see cref="LoopbackEndpointValidator"/> against <see cref="AllowNonLoopbackBind"/>.</summary>
    public required IReadOnlyList<IPEndPoint> BindEndpoints { get; init; }

    /// <summary>
    /// When false (the default), any non-loopback bind endpoint (a LAN IP or a wildcard 0.0.0.0 / ::)
    /// fails fast at startup. When true, non-loopback binds are permitted deliberately.
    /// </summary>
    public bool AllowNonLoopbackBind { get; init; }

    /// <summary>
    /// Optional inbound SMTP AUTH username. When both this and <see cref="AuthPassword"/> are set,
    /// AUTH (PLAIN/LOGIN) is enabled AND required for every session; unauthenticated sessions are
    /// rejected. Both null/empty (the default) disables inbound AUTH entirely.
    /// </summary>
    public string? AuthUsername { get; init; }

    /// <summary>Optional inbound SMTP AUTH password; see <see cref="AuthUsername"/>. Never logged.</summary>
    public string? AuthPassword { get; init; }

    /// <summary>True when both <see cref="AuthUsername"/> and <see cref="AuthPassword"/> are set (non-whitespace).</summary>
    public bool IsInboundAuthConfigured =>
        !string.IsNullOrWhiteSpace(AuthUsername) && !string.IsNullOrWhiteSpace(AuthPassword);

    public string ServerName { get; init; } = "smtpoauth";

    public int MaxMessageSizeBytes { get; init; } = DefaultMaxMessageSizeBytes;

    public int MaxRecipients { get; init; } = DefaultMaxRecipients;

    public TimeSpan IdleTimeout { get; init; } = DefaultIdleTimeout;
}
