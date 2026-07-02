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

    /// <summary>Loopback-only addresses (127.0.0.1 and/or ::1) to bind. Validated by <see cref="LoopbackEndpointValidator"/>.</summary>
    public required IReadOnlyList<IPEndPoint> BindEndpoints { get; init; }

    public string ServerName { get; init; } = "smtpoauth";

    public int MaxMessageSizeBytes { get; init; } = DefaultMaxMessageSizeBytes;

    public int MaxRecipients { get; init; } = DefaultMaxRecipients;

    public TimeSpan IdleTimeout { get; init; } = DefaultIdleTimeout;
}
