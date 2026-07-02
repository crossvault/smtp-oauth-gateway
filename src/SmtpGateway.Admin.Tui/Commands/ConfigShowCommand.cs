using Spectre.Console;
using Spectre.Console.Cli;

namespace SmtpGateway.Admin.Tui.Commands;

/// <summary>
/// 'config show': prints every documented Gateway setting as dotted-path -&gt; value, read directly
/// from appsettings.json. Secrets (ClientSecret, Password, ...) are shown in CLEARTEXT - an
/// explicit product decision, not an oversight.
/// </summary>
public sealed class ConfigShowCommand : Command<GatewayCommandSettings>
{
    protected override int Execute(CommandContext context, GatewayCommandSettings settings, CancellationToken cancellationToken)
    {
        var path = GatewayConfigLoader.ResolvePath(settings.ConfigPath);

        Table table;
        try
        {
            table = ConfigShowRenderer.Build(path);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Failed to load configuration: {ex.Message}[/]");
            return 1;
        }

        AnsiConsole.Write(table);
        return 0;
    }
}
