using Spectre.Console;
using Spectre.Console.Cli;
using SmtpGateway.Infrastructure;

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
            GatewayOptions options;
            try
            {
                options = GatewayConfigLoader.Load(settings.ConfigPath);
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Failed to load configuration: {ex.Message}[/]");
                return 1;
            }

            try
            {
                GatewayOptionsValidator.Validate(options);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Configuration is invalid: {ex.Message}[/]");
                return 1;
            }

            try
            {
                OutboundProviderFactory.Create(options.OutboundProvider);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Outbound provider configuration is invalid: {ex.Message}[/]");
                return 1;
            }

            AnsiConsole.MarkupLine("[green]Configuration is valid.[/]");
            return 0;
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
