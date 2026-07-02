using Spectre.Console;
using Spectre.Console.Cli;
using SmtpGateway.Infrastructure;

namespace SmtpGateway.Admin.Tui.Commands;

/// <summary>
/// 'queue export &lt;id&gt;': reads the item's raw MIME from the spool via <see cref="FileSpool"/>
/// and writes it to the fixed "exports/&lt;id&gt;.eml" path (relative to the current working
/// directory, created if missing). No destination-path prompt.
/// </summary>
public sealed class QueueExportCommand : AsyncCommand<QueueItemIdSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, QueueItemIdSettings settings, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(settings.Id, out var id))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]'{settings.Id}' is not a valid queue item id (expected a GUID).[/]");
            return 1;
        }

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

        var repository = new SqliteQueueRepository(options.QueueDatabasePath);
        var item = await repository.GetByIdAsync(id, cancellationToken);
        if (item is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Queue item '{id}' was not found.[/]");
            return 1;
        }

        var fullPath = await QueueExportAction.ExportAsync(options, item, cancellationToken);

        AnsiConsole.MarkupLineInterpolated($"[green]Queue item '{id}' exported to '{fullPath}'.[/]");
        return 0;
    }
}
