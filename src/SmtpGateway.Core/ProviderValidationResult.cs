namespace SmtpGateway.Core;

/// <summary>
/// Result of an active, non-sending connectivity/health check against an outbound provider (e.g.
/// SMTP connect+TLS+auth+NOOP, or a Graph mailbox-reachability check). A validation failure is a
/// warning only - it is never persisted and never blocks saving/activating a configuration; the
/// caller (the admin TUI) shows this live and discards it afterwards.
/// </summary>
public sealed record ProviderValidationResult(bool Success, string? ErrorMessage, TimeSpan Elapsed);
