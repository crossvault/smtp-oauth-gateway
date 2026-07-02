using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using SmtpGateway.Core;
using SmtpGateway.Infrastructure;

namespace SmtpGateway.Admin.Tui.Commands;

/// <summary>
/// 'status': prints a dashboard with queue depth by status, oldest queued item age, total spool
/// bytes, attempt counts, delivery success/failure counts, poison count, and the currently
/// configured outbound provider.
/// </summary>
public sealed class StatusCommand : AsyncCommand<GatewayCommandSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, GatewayCommandSettings settings, CancellationToken cancellationToken)
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
        var summary = QueueStatusSummary.Build(items, DateTimeOffset.UtcNow);

        var table = new Table().Title("Queue depth by status");
        table.AddColumn("Status");
        table.AddColumn(new TableColumn("Count").RightAligned());
        foreach (var status in Enum.GetValues<QueueItemStatus>())
        {
            table.AddRow(status.ToString(), summary.CountsByStatus[status].ToString(CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);

        var summaryLines = new Rows(
            new Markup($"Oldest queued item age: {FormatAge(summary.OldestQueuedAge)}"),
            new Markup($"Total spool bytes: {summary.TotalSpoolBytes.ToString("N0", CultureInfo.InvariantCulture)}"),
            new Markup($"Total attempts: {summary.TotalAttempts.ToString("N0", CultureInfo.InvariantCulture)}"),
            new Markup($"Recipients sent: {summary.RecipientsSent.ToString("N0", CultureInfo.InvariantCulture)}"),
            new Markup($"Recipients permanently failed: {summary.RecipientsPermanentlyFailed.ToString("N0", CultureInfo.InvariantCulture)}"),
            new Markup($"Poison items: {summary.PoisonCount.ToString("N0", CultureInfo.InvariantCulture)}"),
            // The provider name is an operator-controlled config string; escape it so a value
            // containing markup metacharacters cannot throw or inject escapes into the terminal.
            new Markup($"Outbound provider: {Markup.Escape(options.OutboundProvider.Provider)}"));

        AnsiConsole.Write(new Panel(summaryLines).Header("Summary"));

        return 0;
    }

    private static string FormatAge(TimeSpan? age) => age is { } value ? value.ToString("c", CultureInfo.InvariantCulture) : "n/a";
}
