using Spectre.Console;
using Spectre.Console.Cli;
using System.Text.Json.Nodes;

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

        JsonObject root;
        try
        {
            root = ConfigDocument.LoadRoot(path);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Failed to load configuration: {ex.Message}[/]");
            return 1;
        }

        var gateway = ConfigDocument.GetOrCreateGatewaySection(root);
        var rows = ConfigDocument.Flatten(gateway);

        var table = new Table().Title($"Gateway configuration ('{path}')");
        table.AddColumn("Path");
        table.AddColumn("Value");
        foreach (var (rowPath, value) in rows)
        {
            // Config keys and (cleartext) values are operator-controlled strings that Spectre would
            // otherwise parse as markup - a value containing '[' (e.g. a password) would throw a
            // malformed-markup exception and break `config show`. Escape both cells.
            table.AddRow(Markup.Escape(rowPath), Markup.Escape(value));
        }

        AnsiConsole.Write(table);
        return 0;
    }
}
