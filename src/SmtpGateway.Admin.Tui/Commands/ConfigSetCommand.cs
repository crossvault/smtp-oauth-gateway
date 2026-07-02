using System.Text.Json.Nodes;
using Spectre.Console;
using Spectre.Console.Cli;
using SmtpGateway.Infrastructure;

namespace SmtpGateway.Admin.Tui.Commands;

/// <summary>
/// 'config set &lt;path&gt; &lt;value&gt;': generically sets a single Gateway setting addressed by a dotted
/// path, preserving every unrelated key. appsettings.json is always the source of truth - the
/// write happens unconditionally (no rollback), and a post-write validation pass is only used to
/// report problems clearly, never to undo the write.
/// </summary>
public sealed class ConfigSetCommand : Command<ConfigSetSettings>
{
    protected override int Execute(CommandContext context, ConfigSetSettings settings, CancellationToken cancellationToken)
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

        try
        {
            var gateway = ConfigDocument.GetOrCreateGatewaySection(root);
            ConfigDocument.SetPath(gateway, settings.Path, settings.Value);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Could not set '{settings.Path}': {ex.Message}[/]");
            return 1;
        }

        ConfigDocument.Save(root, path);
        AnsiConsole.MarkupLineInterpolated($"[green]Set '{settings.Path}' in '{path}'.[/]");

        ReportValidation(path);

        AnsiConsole.MarkupLine("[yellow]Restart required: the running service must be restarted to pick up this change.[/]");
        return 0;
    }

    /// <summary>
    /// Best-effort post-write validation report. The write above has already happened - per the
    /// "no rollback" decision, a bad edit is never automatically reverted, only clearly reported.
    /// </summary>
    private static void ReportValidation(string path)
    {
        GatewayOptions options;
        try
        {
            options = GatewayConfigLoader.Load(path);
            GatewayOptionsValidator.Validate(options);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]Warning: configuration is invalid after this change: {ex.Message}[/]");
            return;
        }

        try
        {
            OutboundProviderFactory.Create(options.OutboundProvider);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]Warning: outbound provider configuration is invalid after this change: {ex.Message}[/]");
        }
    }
}
