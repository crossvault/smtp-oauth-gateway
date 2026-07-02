using Spectre.Console;
using Spectre.Console.Cli;
using SmtpGateway.Infrastructure;

namespace SmtpGateway.Admin.Tui.Commands;

/// <summary>
/// 'queue show &lt;id&gt;': prints full detail for one queue item - envelope, per-recipient
/// status/attempts/last error, timestamps, and spool path/hash/size. Never prints the raw MIME
/// body content.
/// </summary>
public sealed class QueueShowCommand : AsyncCommand<QueueItemIdSettings>
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

        // The full detail rendering (with the Markup.Escape rules for untrusted envelope/error/spool
        // strings) lives in the shared QueueItemDetailRenderer so the interactive shell's queue
        // browser presents identical information.
        QueueItemDetailRenderer.Write(item);
        return 0;
    }
}
