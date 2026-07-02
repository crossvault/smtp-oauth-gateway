using Spectre.Console;
using Spectre.Console.Cli;
using SmtpGateway.Infrastructure;

namespace SmtpGateway.Admin.Tui.Commands;

/// <summary>
/// 'queue retry &lt;id&gt;': resets every non-Sent recipient of the item back to Retryable and
/// clears its NextAttemptUtc so it is picked up immediately. No confirmation prompt.
/// </summary>
public sealed class QueueRetryCommand : AsyncCommand<QueueItemIdSettings>
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
            await repository.RetryAsync(id, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Could not retry queue item '{id}': {ex.Message}[/]");
            return 1;
        }

        AnsiConsole.MarkupLineInterpolated($"[green]Queue item '{id}' was reset for retry.[/]");
        return 0;
    }
}
