using Spectre.Console.Testing;
using SmtpGateway.Admin.Tui;
using SmtpGateway.Core;
using SmtpGateway.Infrastructure;
using Xunit;

namespace SmtpGateway.IntegrationTests;

/// <summary>
/// End-to-end tests for the no-args interactive shell, driven through
/// <see cref="TuiTestRunner.RunShell"/> with scripted <see cref="TestConsoleInput"/> keystrokes
/// against a real temp SQLite database and file spool - no mocking. Each SelectionPrompt is
/// navigated with DownArrow to move the highlight and Enter to select; the main menu order is
/// Dashboard(0), Queue(1), Configuration(2), First-time setup(3), Provider test(4), Quit(5).
/// </summary>
public sealed class InteractiveShellTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root;
    private readonly string _configPath;
    private readonly string _spoolDirectory;
    private readonly string _databasePath;

    public InteractiveShellTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "SmtpGateway.InteractiveShellTests", Guid.NewGuid().ToString("N"));
        _spoolDirectory = Path.Combine(_root, "spool");
        _databasePath = Path.Combine(_root, "queue.db");
        _configPath = Path.Combine(_root, "appsettings.json");
        Directory.CreateDirectory(_root);
        WriteValidConfig();
    }

    public void Dispose()
    {
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _databasePath }.ConnectionString))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void WriteValidConfig() =>
        File.WriteAllText(
            _configPath,
            $$"""
            {
              "Gateway": {
                "SpoolDirectory": {{System.Text.Json.JsonSerializer.Serialize(_spoolDirectory)}},
                "QueueDatabasePath": {{System.Text.Json.JsonSerializer.Serialize(_databasePath)}},
                "OutboundProvider": { "Provider": "GenericSmtp" }
              }
            }
            """);

    private async Task<Guid> SeedItemAsync(QueueItemStatus status, RecipientStatus[]? recipientStatuses = null)
    {
        var repository = new SqliteQueueRepository(_databasePath);
        var spool = new FileSpool(_spoolDirectory);

        var id = Guid.NewGuid();
        var write = await spool.WriteAsync(id, "From: sender@example.com\r\n\r\nBody"u8.ToArray(), Ct);

        var statuses = recipientStatuses ?? [RecipientStatus.Pending];
        var addresses = statuses.Select((_, i) => $"rcpt{i}@example.com").ToArray();
        var recipients = addresses.Zip(statuses, (address, s) => new RecipientDelivery(address, s)).ToList();

        var item = new QueueItem
        {
            Id = id,
            Envelope = new Envelope("sender@example.com", addresses),
            Recipients = recipients,
            MimePath = write.Path,
            Hash = write.Hash,
            SizeBytes = write.SizeBytes,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Status = status,
        };

        await repository.EnqueueAsync(item, Ct);
        return id;
    }

    private static void Down(TestConsoleInput input, int count)
    {
        for (var i = 0; i < count; i++)
        {
            input.PushKey(ConsoleKey.DownArrow);
        }
    }

    private static void Enter(TestConsoleInput input) => input.PushKey(ConsoleKey.Enter);

    [Fact]
    public void NoArgs_ShowsMainMenu_AndQuitExitsZero()
    {
        var (exitCode, output) = TuiTestRunner.RunShell(
            i =>
            {
                Down(i, 5); // highlight Quit (index 5)
                Enter(i);   // select Quit
            },
            _configPath);

        Assert.Equal(0, exitCode);
        Assert.Contains("Main menu", output);
        Assert.Contains("Dashboard", output);
        Assert.Contains("Goodbye", output);
    }

    [Fact]
    public async Task Dashboard_RendersQueueCountsFromDatabase_AndReturnsToMenu()
    {
        await SeedItemAsync(QueueItemStatus.Queued);
        await SeedItemAsync(QueueItemStatus.Poison, [RecipientStatus.PermanentlyFailed]);

        var (exitCode, output) = TuiTestRunner.RunShell(
            i =>
            {
                Enter(i);   // main menu: Dashboard (index 0)
                Down(i, 1); // dashboard menu: highlight Back (Refresh=0, Back=1)
                Enter(i);   // select Back -> main menu
                Down(i, 5); // main menu: highlight Quit
                Enter(i);   // select Quit
            },
            _configPath);

        Assert.Equal(0, exitCode);
        Assert.Contains("Poison", output);
        Assert.Contains("Queued", output);
        Assert.Contains("Summary", output);
        // The provider comes from the real config file, proving the dashboard read live data.
        Assert.Contains("GenericSmtp", output);
    }

    [Fact]
    public async Task QueueBrowser_SelectItemAndRetry_ChangesRepositoryState()
    {
        var id = await SeedItemAsync(QueueItemStatus.Poison, [RecipientStatus.PermanentlyFailed]);

        var (exitCode, _) = TuiTestRunner.RunShell(
            i =>
            {
                Down(i, 1); // main menu: highlight Queue (index 1)
                Enter(i);   // select Queue
                Enter(i);   // queue rows: select the seeded item (index 0)
                Enter(i);   // action menu: Retry (index 0)
                Enter(i);   // outcome: Continue
                Down(i, 1); // queue rows: highlight Back (item=0, Back=1)
                Enter(i);   // select Back -> main menu
                Down(i, 5); // main menu: highlight Quit
                Enter(i);   // select Quit
            },
            _configPath);

        Assert.Equal(0, exitCode);

        var repository = new SqliteQueueRepository(_databasePath);
        var item = await repository.GetByIdAsync(id, Ct);
        Assert.NotNull(item);
        Assert.NotEqual(QueueItemStatus.Poison, item!.Status);
        Assert.All(item.Recipients, r => Assert.Equal(RecipientStatus.Retryable, r.Status));
    }

    [Fact]
    public void BrokenConfig_ShowsFriendlyPanel_ShellStillUsable()
    {
        File.WriteAllText(_configPath, "{ \"Gateway\": { this-is-not-valid-json ");

        var (exitCode, output) = TuiTestRunner.RunShell(
            i =>
            {
                Down(i, 5); // main menu still renders: highlight Quit
                Enter(i);   // select Quit
            },
            _configPath);

        Assert.Equal(0, exitCode);
        Assert.Contains("Configuration problem", output);
        Assert.Contains("First-time setup", output);
    }
}
