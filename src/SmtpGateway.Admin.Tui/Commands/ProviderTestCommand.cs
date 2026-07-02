using Spectre.Console;
using Spectre.Console.Cli;
using SmtpGateway.Infrastructure;

namespace SmtpGateway.Admin.Tui.Commands;

/// <summary>
/// 'provider test [--timeout &lt;seconds&gt;]': runs an active, non-sending connectivity/health check
/// against the currently configured outbound provider (SMTP connect+TLS+auth+NOOP for
/// GenericSmtp/M365Oauth, a mailbox-reachability check for Graph). A failure here is a WARNING
/// ONLY - this command always exits 0, since a validation failure must never block or fail
/// anything (it is shown live and never persisted). The actual check lives in
/// <see cref="ProviderConnectivityCheck"/>, shared with the 'setup' wizard.
/// </summary>
public sealed class ProviderTestCommand : AsyncCommand<ProviderTestSettings>
{
    public const int DefaultTimeoutSeconds = ProviderConnectivityCheck.DefaultTimeoutSeconds;

    protected override async Task<int> ExecuteAsync(CommandContext context, ProviderTestSettings settings, CancellationToken cancellationToken)
    {
        GatewayOptions options;
        try
        {
            options = GatewayConfigLoader.Load(settings.ConfigPath);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Failed to load configuration: {ex.Message}[/]");
            return 1;
        }

        var timeoutSeconds = settings.TimeoutSeconds ?? DefaultTimeoutSeconds;
        await ProviderConnectivityCheck.RunAsync(options.OutboundProvider, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
