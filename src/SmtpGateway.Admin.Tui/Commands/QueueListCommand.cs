using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using SmtpGateway.Core;
using SmtpGateway.Infrastructure;

namespace SmtpGateway.Admin.Tui.Commands;

/// <summary>
/// 'queue list [--status &lt;status&gt;]': lists queue items (id, status, recipient count, created
/// age, attempt count), optionally filtered by <see cref="SmtpGateway.Core.QueueItemStatus"/>.
/// Discarded items are hidden from the default (unfiltered) listing - queue history is never
/// deleted, but an administrator-discarded item should not clutter the everyday view; pass
/// <c>--status Discarded</c> to see them explicitly.
/// </summary>
public sealed class QueueListCommand : AsyncCommand<QueueListSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, QueueListSettings settings, CancellationToken cancellationToken)
    {
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
        var items = await repository.ListAsync(cancellationToken);

        items = settings.Status is { } status
            ? items.Where(item => item.Status == status).ToList()
            : items.Where(item => item.Status != QueueItemStatus.Discarded).ToList();

        var now = DateTimeOffset.UtcNow;
        var table = new Table();
        table.AddColumn("Id");
        table.AddColumn("Status");
        table.AddColumn(new TableColumn("Recipients").RightAligned());
        table.AddColumn("Created age");
        table.AddColumn(new TableColumn("Attempts").RightAligned());

        foreach (var item in items)
        {
            table.AddRow(
                item.Id.ToString(),
                item.Status.ToString(),
                item.Recipients.Count.ToString(CultureInfo.InvariantCulture),
                (now - item.CreatedAtUtc).ToString("c", CultureInfo.InvariantCulture),
                item.AttemptCount.ToString(CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);
        return 0;
    }
}
