using Spectre.Console;
using SmtpGateway.Core;
using SmtpGateway.Infrastructure;

namespace SmtpGateway.Admin.Tui.Commands;

/// <summary>
/// Shared, warnings-only outbound-provider connectivity/health check used by both
/// 'provider test' and the 'setup' wizard's optional post-save probe. Builds the configured
/// <see cref="IOutboundProvider"/> and runs its active, non-sending check
/// (SMTP connect+TLS+auth+NOOP for GenericSmtp/M365Oauth, mailbox reachability for Graph).
/// A failure here is a WARNING ONLY - this never throws and never signals a non-zero outcome,
/// since a connectivity failure must never block or fail configuration.
/// </summary>
public static class ProviderConnectivityCheck
{
    public const int DefaultTimeoutSeconds = 10;

    /// <summary>
    /// Runs the connectivity check and writes its result (green on success, yellow warning on
    /// failure) to the current <see cref="AnsiConsole"/>. Every exception is caught and reported
    /// as a warning; this method always returns normally.
    /// </summary>
    public static async Task RunAsync(OutboundProviderOptions provider, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        ProviderValidationResult result;
        try
        {
            IOutboundProvider built;
            try
            {
                built = OutboundProviderFactory.Create(provider);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]Warning: could not build the outbound provider: {ex.Message}[/]");
                return;
            }

            result = built switch
            {
                GenericSmtpProvider generic => await generic.TestConnectionAsync(linkedCts.Token).ConfigureAwait(false),
                GraphSendMailProvider graph => await graph.TestMailboxAccessAsync(linkedCts.Token).ConfigureAwait(false),
                _ => new ProviderValidationResult(
                    false, $"No connectivity check is available for provider type '{built.GetType().Name}'.", TimeSpan.Zero),
            };
        }
        catch (Exception ex)
        {
            // Defensive: the underlying checks already catch their own exceptions, but this must
            // never let an unanticipated exception turn a warnings-only check into a hard failure.
            AnsiConsole.MarkupLineInterpolated($"[yellow]Warning: provider check threw unexpectedly: {ex.Message}[/]");
            return;
        }

        if (result.Success)
        {
            AnsiConsole.MarkupLineInterpolated($"[green]Provider check succeeded in {result.Elapsed}.[/]");
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]Warning: provider check failed after {result.Elapsed}: {result.ErrorMessage}[/]");
        }
    }
}
