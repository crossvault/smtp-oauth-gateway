using Spectre.Console;
using Spectre.Console.Cli;
using SmtpGateway.Infrastructure;

namespace SmtpGateway.Admin.Tui.Commands;

/// <summary>
/// 'queue discard &lt;id&gt;': marks the item Discarded so it is never leased again, without
/// deleting it from queue history. No confirmation prompt - this is an explicit product decision.
/// </summary>
public sealed class QueueDiscardCommand : AsyncCommand<QueueItemIdSettings>
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
        try
        {
            await repository.DiscardAsync(id, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Could not discard queue item '{id}': {ex.Message}[/]");
            return 1;
        }

        AnsiConsole.MarkupLineInterpolated($"[green]Queue item '{id}' was discarded.[/]");
        return 0;
    }
}
