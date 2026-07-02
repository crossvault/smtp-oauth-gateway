using Spectre.Console;
using SmtpGateway.Admin.Tui;
using SmtpGateway.Core;
using SmtpGateway.Infrastructure;
using Spectre.Console.Cli.Testing;
using Xunit;

namespace SmtpGateway.IntegrationTests;

/// <summary>
/// End-to-end command-wiring tests: each test runs the real <see cref="AdminTuiApp"/> command
/// tree (the exact configuration <c>Program.cs</c> uses) via <see cref="CommandAppTester"/>
/// against a real temp SQLite database and a real temp file spool - no mocking of
/// <see cref="SqliteQueueRepository"/> or <see cref="FileSpool"/>.
/// </summary>
public sealed class AdminTuiCommandTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root;
    private readonly string _configPath;
    private readonly string _spoolDirectory;
    private readonly string _databasePath;

    public AdminTuiCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "SmtpGateway.AdminTuiCommandTests", Guid.NewGuid().ToString("N"));
        _spoolDirectory = Path.Combine(_root, "spool");
        _databasePath = Path.Combine(_root, "queue.db");
        _configPath = Path.Combine(_root, "appsettings.json");
        Directory.CreateDirectory(_root);

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
    }

    public void Dispose()
    {
        // Clear only this test's own connection pool (scoped by connection string) rather than
        // the process-global ClearAllPools(), which would race with other test classes
        // concurrently opening/closing pooled connections for their own unrelated databases.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _databasePath }.ConnectionString))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        var exportPath = ExportPathBuilder.BuildPath(_lastExportedId ?? Guid.Empty);
        if (_lastExportedId is not null && File.Exists(exportPath))
        {
            File.Delete(exportPath);
        }
    }

    private Guid? _lastExportedId;

    /// <summary>Runs the real <see cref="AdminTuiApp"/> command tree; see <see cref="TuiTestRunner"/>.</summary>
    private static (int ExitCode, string Output) RunCommand(params string[] args) => TuiTestRunner.Run(args);

    private async Task<Guid> SeedItemAsync(
        QueueItemStatus status = QueueItemStatus.Queued,
        RecipientStatus[]? recipientStatuses = null,
        byte[]? rawMime = null)
    {
        var repository = new SqliteQueueRepository(_databasePath);
        var spool = new FileSpool(_spoolDirectory);

        var id = Guid.NewGuid();
        var write = await spool.WriteAsync(id, rawMime ?? "From: sender@example.com\r\n\r\nBody"u8.ToArray(), Ct);

        var statuses = recipientStatuses ?? [RecipientStatus.Pending];
        var addresses = statuses.Select((_, i) => $"rcpt{i}@example.com").ToArray();
        var recipients = addresses.Zip(statuses, (address, status) => new RecipientDelivery(address, status)).ToList();

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

        // EnqueueAsync always inserts the item's own Status verbatim, so items seeded with a
        // non-Queued status (Poison, Discarded, PartiallySent, ...) round-trip exactly as given -
        // no extra fixup needed here.
        return id;
    }

    [Fact]
    public async Task Status_PrintsQueueDepthAndProviderInfo()
    {
        await SeedItemAsync(QueueItemStatus.Queued);
        await SeedItemAsync(QueueItemStatus.Poison);

        var result = RunCommand("status", "--config", _configPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Queued", result.Output);
        Assert.Contains("Poison", result.Output);
        Assert.Contains("GenericSmtp", result.Output);
    }

    [Fact]
    public async Task QueueList_NoFilter_ListsAllItems()
    {
        var id1 = await SeedItemAsync(QueueItemStatus.Queued);
        var id2 = await SeedItemAsync(QueueItemStatus.Poison);

        var result = RunCommand("queue", "list", "--config", _configPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(id1.ToString(), result.Output);
        Assert.Contains(id2.ToString(), result.Output);
    }

    [Fact]
    public async Task QueueList_FilteredByPartiallySent_OnlyReturnsPartiallySentItems()
    {
        var partiallySentId = await SeedItemAsync(
            QueueItemStatus.PartiallySent,
            recipientStatuses: [RecipientStatus.Sent, RecipientStatus.Retryable]);
        var queuedId = await SeedItemAsync(QueueItemStatus.Queued);

        var result = RunCommand("queue", "list", "--config", _configPath, "--status", "PartiallySent");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(partiallySentId.ToString(), result.Output);
        Assert.DoesNotContain(queuedId.ToString(), result.Output);
    }

    [Fact]
    public async Task QueueShow_PrintsEnvelopeAndRecipientDetail()
    {
        var id = await SeedItemAsync(QueueItemStatus.Queued);

        var result = RunCommand("queue", "show", id.ToString(), "--config", _configPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(id.ToString(), result.Output);
        Assert.Contains("sender@example.com", result.Output);
        Assert.Contains("rcpt0@example.com", result.Output);
    }

    [Fact]
    public void QueueShow_UnknownId_PrintsErrorAndReturnsNonZero()
    {
        var result = RunCommand("queue", "show", Guid.NewGuid().ToString(), "--config", _configPath);

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task QueueShow_AddressLiteralRecipient_RendersWithoutThrowing()
    {
        // a@[10.0.0.1] is an RFC-valid address literal that SmtpServer stores brackets-included.
        // The '[' is a Spectre markup metacharacter; rendering it unescaped throws a malformed-markup
        // exception, breaking `queue show` for exactly the item an operator needs to inspect.
        var id = await SeedItemWithRecipientsAsync("sender@example.com", ["a@[10.0.0.1]"]);

        var result = RunCommand("queue", "show", id.ToString(), "--config", _configPath);

        Assert.Equal(0, result.ExitCode);
        // The stored address is displayed verbatim (escaping only neutralizes the markup meaning).
        Assert.Contains("a@[10.0.0.1]", result.Output);
    }

    private async Task<Guid> SeedItemWithRecipientsAsync(string mailFrom, string[] recipients)
    {
        var repository = new SqliteQueueRepository(_databasePath);
        var spool = new FileSpool(_spoolDirectory);
        var id = Guid.NewGuid();
        var write = await spool.WriteAsync(id, "From: sender@example.com\r\n\r\nBody"u8.ToArray(), Ct);

        var item = new QueueItem
        {
            Id = id,
            Envelope = new Envelope(mailFrom, recipients),
            Recipients = recipients.Select(r => new RecipientDelivery(r)).ToList(),
            MimePath = write.Path,
            Hash = write.Hash,
            SizeBytes = write.SizeBytes,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Status = QueueItemStatus.Queued,
        };

        await repository.EnqueueAsync(item, Ct);
        return id;
    }

    [Fact]
    public async Task QueueRetry_PoisonItem_BecomesLeasableAgain()
    {
        var id = await SeedItemAsync(
            QueueItemStatus.Poison,
            recipientStatuses: [RecipientStatus.PermanentlyFailed]);

        var result = RunCommand("queue", "retry", id.ToString(), "--config", _configPath);

        Assert.Equal(0, result.ExitCode);

        var repository = new SqliteQueueRepository(_databasePath);
        var item = await repository.GetByIdAsync(id, Ct);
        Assert.NotNull(item);
        Assert.NotEqual(QueueItemStatus.Poison, item!.Status);
        Assert.All(item.Recipients, r => Assert.Equal(RecipientStatus.Retryable, r.Status));

        var leased = await repository.TryLeaseNextAsync("test-worker", TimeSpan.FromMinutes(5), Ct);
        Assert.NotNull(leased);
        Assert.Equal(id, leased!.Id);
    }

    [Fact]
    public async Task QueueDiscard_ItemDisappearsFromDefaultListButStaysVisibleWithFilter()
    {
        var id = await SeedItemAsync(QueueItemStatus.Queued);

        var discardResult = RunCommand("queue", "discard", id.ToString(), "--config", _configPath);
        Assert.Equal(0, discardResult.ExitCode);

        var defaultList = RunCommand("queue", "list", "--config", _configPath);
        Assert.DoesNotContain(id.ToString(), defaultList.Output);

        var filteredList = RunCommand("queue", "list", "--config", _configPath, "--status", "Discarded");
        Assert.Contains(id.ToString(), filteredList.Output);

        var repository = new SqliteQueueRepository(_databasePath);
        var leased = await repository.TryLeaseNextAsync("test-worker", TimeSpan.FromMinutes(5), Ct);
        Assert.Null(leased);
    }

    [Fact]
    public async Task QueueExport_WritesRawMimeToFixedExportsDirectory()
    {
        var rawMime = "From: sender@example.com\r\nTo: rcpt0@example.com\r\n\r\nExport test body"u8.ToArray();
        var id = await SeedItemAsync(QueueItemStatus.Queued, rawMime: rawMime);
        _lastExportedId = id;

        var result = RunCommand("queue", "export", id.ToString(), "--config", _configPath);

        Assert.Equal(0, result.ExitCode);

        var exportPath = ExportPathBuilder.BuildPath(id);
        Assert.True(File.Exists(exportPath), $"Expected export file at '{exportPath}'.");
        var exportedBytes = await File.ReadAllBytesAsync(exportPath, Ct);
        Assert.Equal(rawMime, exportedBytes);
    }

    [Fact]
    public void QueueExport_UnknownId_PrintsErrorAndReturnsNonZero()
    {
        var result = RunCommand("queue", "export", Guid.NewGuid().ToString(), "--config", _configPath);

        Assert.NotEqual(0, result.ExitCode);
    }
}
