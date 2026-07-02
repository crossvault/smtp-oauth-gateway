using Spectre.Console;
using Spectre.Console.Cli;

namespace SmtpGateway.Admin.Tui.Commands;

/// <summary>
/// 'config validate': re-reads appsettings.json, binds it to <see cref="GatewayOptions"/>, and
/// runs <see cref="GatewayOptionsValidator"/> plus <see cref="OutboundProviderFactory.Create"/>.
/// Never lets an exception reach the console unhandled - every failure is reported as a clear,
/// single-line error instead.
/// </summary>
public sealed class ConfigValidateCommand : Command<GatewayCommandSettings>
{
    protected override int Execute(CommandContext context, GatewayCommandSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            // The validation sequence itself lives in the shared ConfigValidation helper so the
            // interactive shell's configuration screen reports the exact same outcome.
            var result = ConfigValidation.Run(settings.ConfigPath);
            if (result.Success)
            {
                AnsiConsole.MarkupLineInterpolated($"[green]{result.Message}[/]");
                return 0;
            }

            AnsiConsole.MarkupLineInterpolated($"[red]{result.Message}[/]");
            return 1;
        }
        catch (Exception ex)
        {
            // Defensive final catch-all: an unanticipated exception must still be reported as a
            // clear validation failure, never an unhandled crash.
            AnsiConsole.MarkupLineInterpolated($"[red]Configuration validation failed unexpectedly: {ex.Message}[/]");
            return 1;
        }
    }
}
