using Spectre.Console;
using Spectre.Console.Cli;
using SmtpGateway.Core;
using SmtpGateway.Infrastructure;

namespace SmtpGateway.Admin.Tui.Commands;

/// <summary>
/// 'provider test [--timeout &lt;seconds&gt;]': runs an active, non-sending connectivity/health check
/// against the currently configured outbound provider (SMTP connect+TLS+auth+NOOP for
/// GenericSmtp/M365Oauth, a mailbox-reachability check for Graph). A failure here is a WARNING
/// ONLY - this command always exits 0, since a validation failure must never block or fail
/// anything (it is shown live and never persisted).
/// </summary>
public sealed class ProviderTestCommand : AsyncCommand<ProviderTestSettings>
{
    public const int DefaultTimeoutSeconds = 10;

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
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        ProviderValidationResult result;
        try
        {
            IOutboundProvider provider;
            try
            {
                provider = OutboundProviderFactory.Create(options.OutboundProvider);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]Warning: could not build the outbound provider: {ex.Message}[/]");
                return 0;
            }

            result = provider switch
            {
                GenericSmtpProvider generic => await generic.TestConnectionAsync(linkedCts.Token).ConfigureAwait(false),
                GraphSendMailProvider graph => await graph.TestMailboxAccessAsync(linkedCts.Token).ConfigureAwait(false),
                _ => new ProviderValidationResult(
                    false, $"No connectivity check is available for provider type '{provider.GetType().Name}'.", TimeSpan.Zero),
            };
        }
        catch (Exception ex)
        {
            // Defensive: TestConnectionAsync/TestMailboxAccessAsync already catch their own
            // exceptions and return a failed ProviderValidationResult, but this must never let an
            // unanticipated exception turn a warnings-only check into a hard command failure.
            AnsiConsole.MarkupLineInterpolated($"[yellow]Warning: provider check threw unexpectedly: {ex.Message}[/]");
            return 0;
        }

        if (result.Success)
        {
            AnsiConsole.MarkupLineInterpolated($"[green]Provider check succeeded in {result.Elapsed}.[/]");
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]Warning: provider check failed after {result.Elapsed}: {result.ErrorMessage}[/]");
        }

        return 0;
    }
}
