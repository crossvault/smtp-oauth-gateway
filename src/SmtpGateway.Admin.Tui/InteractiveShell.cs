using System.Globalization;
using System.Reflection;
using Spectre.Console;
using Spectre.Console.Rendering;
using SmtpGateway.Admin.Tui.Commands;
using SmtpGateway.Core;
using SmtpGateway.Infrastructure;

namespace SmtpGateway.Admin.Tui;

/// <summary>
/// The polished interactive menu shell launched when the admin tool is run with no arguments.
/// It is a single flat loop of small screen methods - no navigation framework, no view-model layer,
/// no background refresh timers - that reuses the exact same repository/spool/config/provider code
/// paths as the scripting CLI commands (via the shared <c>*Renderer</c>/<c>*Filter</c>/<c>*Action</c>
/// helpers), rendered through Spectre.Console for a GUI-like feel. Every dynamic string is escaped
/// with <see cref="Markup.Escape"/> because envelope addresses and config values are
/// attacker-influenced. Any argument at all bypasses this shell entirely, keeping scripting behaviour
/// untouched.
/// </summary>
public static class InteractiveShell
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private static readonly Color AccentColor = Color.DeepSkyBlue1;
    private static readonly Style AccentStyle = new(AccentColor);
    private const string Accent = "deepskyblue1";

    private const string MenuDashboard = "Dashboard";
    private const string MenuQueue = "Queue";
    private const string MenuConfig = "Configuration";
    private const string MenuSetup = "First-time setup";
    private const string MenuProvider = "Provider test";
    private const string MenuQuit = "Quit";

    private const string Refresh = "Refresh";
    private const string Back = "Back to main menu";
    private const string ContinueLabel = "Continue";

    private const string CfgShow = "Show";
    private const string CfgValidate = "Validate";

    private const string ActRetry = "Retry";
    private const string ActDiscard = "Discard";
    private const string ActExport = "Export";
    private const string ActBack = "Back to queue";

    /// <summary>
    /// Runs the shell loop until the operator chooses Quit, returning exit code 0. The optional
    /// <paramref name="configPath"/> mirrors the '--config' option of the commands (defaulting to
    /// appsettings.json next to the exe); Program.cs passes null for the default.
    /// </summary>
    public static async Task<int> RunAsync(string? configPath = null, CancellationToken cancellationToken = default)
    {
        var resolvedPath = GatewayConfigLoader.ResolvePath(configPath);

        while (true)
        {
            AnsiConsole.Clear();
            RenderHeader(resolvedPath);

            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title($"[{Accent}]Main menu[/] - choose an action:")
                .HighlightStyle(AccentStyle)
                .AddChoices(MenuDashboard, MenuQueue, MenuConfig, MenuSetup, MenuProvider, MenuQuit));

            switch (choice)
            {
                case MenuDashboard:
                    await DashboardScreen(configPath, cancellationToken).ConfigureAwait(false);
                    break;
                case MenuQueue:
                    await QueueScreen(configPath, cancellationToken).ConfigureAwait(false);
                    break;
                case MenuConfig:
                    ConfigScreen(configPath);
                    break;
                case MenuSetup:
                    await SetupScreen(configPath, cancellationToken).ConfigureAwait(false);
                    break;
                case MenuProvider:
                    await ProviderScreen(configPath, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    AnsiConsole.MarkupLine("[grey]Goodbye.[/]");
                    return 0;
            }
        }
    }

    // --- Header -------------------------------------------------------------------------------

    private static void RenderHeader(string resolvedPath)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

        AnsiConsole.Write(new FigletText("SmtpGateway").Color(AccentColor).Centered());
        AnsiConsole.Write(new Rule($"[grey]v{Markup.Escape(version)}  ·  config:[/] [italic grey]{Markup.Escape(resolvedPath)}[/]")
            .RuleStyle(AccentStyle)
            .Centered());

        // A broken/missing config must never crash the shell: surface a friendly panel pointing at
        // First-time setup, but still let the menu (Setup + Quit) render below.
        try
        {
            _ = GatewayConfigLoader.Load(resolvedPath);
        }
        catch (Exception ex)
        {
            AnsiConsole.Write(new Panel(new Markup(
                $"[yellow]Configuration could not be loaded:[/]\n[red]{Markup.Escape(ex.Message)}[/]\n\n" +
                "[grey]Choose [bold]First-time setup[/] to create or repair it. Other screens will show this error until it is fixed.[/]"))
            {
                Header = new PanelHeader("[yellow] Configuration problem [/]"),
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.Yellow),
            });
        }

        AnsiConsole.WriteLine();
    }

    // --- Dashboard ----------------------------------------------------------------------------

    private static async Task DashboardScreen(string? configPath, CancellationToken ct)
    {
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule($"[{Accent}]Dashboard[/]").LeftJustified().RuleStyle(AccentStyle));

            if (!TryLoadOptions(configPath, out var options))
            {
                return;
            }

            QueueStatusSummary summary;
            try
            {
                summary = await AnsiConsole.Status().StartAsync("Reading queue database...", async _ =>
                {
                    var repository = new SqliteQueueRepository(options.QueueDatabasePath);
                    var items = await repository.ListAsync(ct).ConfigureAwait(false);
                    return QueueStatusSummary.Build(items, DateTimeOffset.UtcNow);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ShowError("Could not read the queue database", ex);
                Continue();
                return;
            }

            RenderDashboard(summary, options.OutboundProvider.Provider);

            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title("Dashboard:")
                .HighlightStyle(AccentStyle)
                .AddChoices(Refresh, Back));
            if (choice == Back)
            {
                return;
            }
        }
    }

    private static void RenderDashboard(QueueStatusSummary summary, string provider)
    {
        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        grid.AddColumn();

        var panels = new List<IRenderable>();
        foreach (var status in Enum.GetValues<QueueItemStatus>())
        {
            var (markup, border) = StatusStyle(status);
            var count = summary.CountsByStatus[status].ToString("N0", Ci);
            // The status label lives in the panel body (not just a header): grid columns size to
            // their content, so a header-only label on a single-digit count would be clipped.
            panels.Add(new Panel(new Markup($"[grey]{status}[/]\n[bold {markup}]{count}[/]").Centered())
            {
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(border),
                Expand = true,
            });
        }

        // Pad to a whole number of 3-wide rows so Grid.AddRow always gets a matching column count.
        while (panels.Count % 3 != 0)
        {
            panels.Add(new Text(string.Empty));
        }

        for (var i = 0; i < panels.Count; i += 3)
        {
            grid.AddRow(panels[i], panels[i + 1], panels[i + 2]);
        }

        AnsiConsole.Write(grid);

        AnsiConsole.Write(new Panel(new Rows(
            new Markup($"[grey]Oldest queued age:[/] {FormatAge(summary.OldestQueuedAge)}"),
            new Markup($"[grey]Total spool bytes:[/] {summary.TotalSpoolBytes.ToString("N0", Ci)}"),
            new Markup($"[grey]Total attempts:[/] {summary.TotalAttempts.ToString("N0", Ci)}"),
            new Markup($"[green]Recipients sent:[/] {summary.RecipientsSent.ToString("N0", Ci)}"),
            new Markup($"[red]Recipients permanently failed:[/] {summary.RecipientsPermanentlyFailed.ToString("N0", Ci)}"),
            new Markup($"[red]Poison items:[/] {summary.PoisonCount.ToString("N0", Ci)}"),
            new Markup($"[grey]Outbound provider:[/] {Markup.Escape(provider)}")))
        {
            Header = new PanelHeader("Summary"),
            Border = BoxBorder.Rounded,
            BorderStyle = AccentStyle,
        });
    }

    // --- Queue browser ------------------------------------------------------------------------

    private static async Task QueueScreen(string? configPath, CancellationToken ct)
    {
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule($"[{Accent}]Queue browser[/]").LeftJustified().RuleStyle(AccentStyle));

            if (!TryLoadOptions(configPath, out var options))
            {
                return;
            }

            var repository = new SqliteQueueRepository(options.QueueDatabasePath);

            IReadOnlyList<QueueItem> items;
            try
            {
                items = await AnsiConsole.Status().StartAsync("Reading queue...", async _ =>
                    QueueListFilter.Filter(await repository.ListAsync(ct).ConfigureAwait(false), null)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ShowError("Could not read the queue", ex);
                Continue();
                return;
            }

            RenderQueueTable(items);

            var now = DateTimeOffset.UtcNow;
            var choices = items.Select(item => new QueueChoice(item, FormatRowLabel(item, now))).ToList();
            choices.Add(new QueueChoice(null, Back));

            var selected = AnsiConsole.Prompt(new SelectionPrompt<QueueChoice>()
                .Title("Select a queue item:")
                .HighlightStyle(AccentStyle)
                .UseConverter(choice => choice.Label)
                .AddChoices(choices));

            if (selected.Item is null)
            {
                return;
            }

            await QueueItemActions(repository, options, selected.Item, ct).ConfigureAwait(false);
        }
    }

    private static void RenderQueueTable(IReadOnlyList<QueueItem> items)
    {
        if (items.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]The queue is empty.[/]");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var table = new Table().Border(TableBorder.Rounded).Title($"[{Accent}]Queue items[/]");
        table.AddColumn("Id");
        table.AddColumn("Status");
        table.AddColumn(new TableColumn("Recipients").RightAligned());
        table.AddColumn("Created age");
        table.AddColumn(new TableColumn("Attempts").RightAligned());

        foreach (var item in items)
        {
            var (markup, _) = StatusStyle(item.Status);
            table.AddRow(
                new Markup(Markup.Escape(item.Id.ToString())),
                new Markup($"[{markup}]{item.Status}[/]"),
                new Markup(item.Recipients.Count.ToString(Ci)),
                new Markup(Markup.Escape((now - item.CreatedAtUtc).ToString("c", Ci))),
                new Markup(item.AttemptCount.ToString(Ci)));
        }

        AnsiConsole.Write(table);
    }

    private static string FormatRowLabel(QueueItem item, DateTimeOffset now)
    {
        var age = (now - item.CreatedAtUtc).ToString("c", Ci);
        var recipients = string.Join(", ", item.Envelope.Recipients);
        // The whole label is escaped because recipient addresses are attacker-influenced and the
        // SelectionPrompt renders choice text as markup.
        return Markup.Escape($"{item.Id}  [{item.Status}]  {age}  {recipients}");
    }

    private static async Task QueueItemActions(SqliteQueueRepository repository, GatewayOptions options, QueueItem item, CancellationToken ct)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule($"[{Accent}]Queue item[/]").LeftJustified().RuleStyle(AccentStyle));
        QueueItemDetailRenderer.Write(item);

        // The menu selection IS the action - there is no extra confirmation prompt for retry/discard,
        // matching the CLI's documented product rule.
        var action = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("Action:")
            .HighlightStyle(AccentStyle)
            .AddChoices(ActRetry, ActDiscard, ActExport, ActBack));

        switch (action)
        {
            case ActRetry:
                await RunRepositoryAction("Retrying...", () => repository.RetryAsync(item.Id, ct),
                    $"Queue item '{item.Id}' was reset for retry.").ConfigureAwait(false);
                break;
            case ActDiscard:
                await RunRepositoryAction("Discarding...", () => repository.DiscardAsync(item.Id, ct),
                    $"Queue item '{item.Id}' was discarded.").ConfigureAwait(false);
                break;
            case ActExport:
                try
                {
                    var path = await AnsiConsole.Status().StartAsync("Exporting...", async _ =>
                        await QueueExportAction.ExportAsync(options, item, ct).ConfigureAwait(false)).ConfigureAwait(false);
                    AnsiConsole.MarkupLineInterpolated($"[green]Queue item '{item.Id}' exported to '{path}'.[/]");
                }
                catch (Exception ex)
                {
                    ShowError("Export failed", ex);
                }

                Continue();
                break;
            default:
                return;
        }
    }

    private static async Task RunRepositoryAction(string spinner, Func<Task> action, string successMessage)
    {
        try
        {
            await AnsiConsole.Status().StartAsync(spinner, async _ => await action().ConfigureAwait(false)).ConfigureAwait(false);
            AnsiConsole.MarkupLineInterpolated($"[green]{successMessage}[/]");
        }
        catch (Exception ex)
        {
            ShowError("Action failed", ex);
        }

        Continue();
    }

    // --- Configuration ------------------------------------------------------------------------

    private static void ConfigScreen(string? configPath)
    {
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule($"[{Accent}]Configuration[/]").LeftJustified().RuleStyle(AccentStyle));

            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title("Configuration:")
                .HighlightStyle(AccentStyle)
                .AddChoices(CfgShow, CfgValidate, Back));

            var path = GatewayConfigLoader.ResolvePath(configPath);
            switch (choice)
            {
                case CfgShow:
                    try
                    {
                        AnsiConsole.Write(ConfigShowRenderer.Build(path));
                    }
                    catch (Exception ex)
                    {
                        ShowError("Failed to load configuration", ex);
                    }

                    Continue();
                    break;
                case CfgValidate:
                    var result = ConfigValidation.Run(configPath);
                    var color = result.Success ? Color.Green : Color.Red;
                    var markup = result.Success ? "green" : "red";
                    AnsiConsole.Write(new Panel(new Markup($"[{markup}]{Markup.Escape(result.Message)}[/]"))
                    {
                        Header = new PanelHeader(result.Success ? "[green] Valid [/]" : "[red] Invalid [/]"),
                        Border = BoxBorder.Rounded,
                        BorderStyle = new Style(color),
                    });
                    Continue();
                    break;
                default:
                    return;
            }
        }
    }

    // --- First-time setup ---------------------------------------------------------------------

    private static async Task SetupScreen(string? configPath, CancellationToken ct)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule($"[{Accent}]First-time setup[/]").LeftJustified().RuleStyle(AccentStyle));

        try
        {
            await WizardCommand.RunAsync(configPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ShowError("Setup failed", ex);
        }

        Continue();
    }

    // --- Provider test ------------------------------------------------------------------------

    private static async Task ProviderScreen(string? configPath, CancellationToken ct)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule($"[{Accent}]Provider test[/]").LeftJustified().RuleStyle(AccentStyle));

        if (!TryLoadOptions(configPath, out var options))
        {
            return;
        }

        // Warnings-only semantics are unchanged: ProviderConnectivityCheck writes the green/yellow
        // outcome itself and never throws or signals failure.
        await AnsiConsole.Status().StartAsync("Checking outbound provider connectivity...", async _ =>
            await ProviderConnectivityCheck.RunAsync(options.OutboundProvider, ProviderConnectivityCheck.DefaultTimeoutSeconds, ct).ConfigureAwait(false))
            .ConfigureAwait(false);

        Continue();
    }

    // --- Shared helpers -----------------------------------------------------------------------

    private static bool TryLoadOptions(string? configPath, out GatewayOptions options)
    {
        try
        {
            options = GatewayConfigLoader.Load(configPath);
            return true;
        }
        catch (Exception ex)
        {
            options = null!;
            AnsiConsole.Write(new Panel(new Markup(
                $"[red]{Markup.Escape(ex.Message)}[/]\n\n[grey]Run [bold]First-time setup[/] from the main menu to create a valid configuration.[/]"))
            {
                Header = new PanelHeader("[red] Configuration error [/]"),
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.Red),
            });
            Continue();
            return false;
        }
    }

    private static void ShowError(string title, Exception ex) =>
        AnsiConsole.Write(new Panel(new Markup($"[red]{Markup.Escape(ex.Message)}[/]"))
        {
            Header = new PanelHeader($"[red] {Markup.Escape(title)} [/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Red),
        });

    private static void Continue() =>
        AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("[grey]Press Enter to continue[/]")
            .HighlightStyle(AccentStyle)
            .AddChoices(ContinueLabel));

    private static string FormatAge(TimeSpan? age) => age is { } value ? value.ToString("c", Ci) : "n/a";

    private static (string Markup, Color Border) StatusStyle(QueueItemStatus status) => status switch
    {
        QueueItemStatus.Sent => ("green", Color.Green),
        QueueItemStatus.Queued
            or QueueItemStatus.Leased
            or QueueItemStatus.Sending
            or QueueItemStatus.RetryScheduled
            or QueueItemStatus.PartiallySent => ("yellow", Color.Yellow),
        QueueItemStatus.Poison or QueueItemStatus.Expired => ("red", Color.Red),
        _ => ("grey", Color.Grey),
    };

    private sealed record QueueChoice(QueueItem? Item, string Label);
}
