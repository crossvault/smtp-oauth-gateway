namespace SmtpGateway.Service;

/// <summary>
/// The dedicated logger category for service lifecycle events (start/stop) and fatal errors.
/// Program.cs filters the Windows EventLog provider down to exactly this category (at
/// Information+) plus Critical-level logs from any category - routine per-message delivery logs
/// use their own class-based categories instead and never reach EventLog.
/// </summary>
public static class LifecycleLog
{
    public const string CategoryName = "SmtpGateway.Lifecycle";
}
