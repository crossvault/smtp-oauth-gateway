using System.Net;
using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Data.Sqlite;
using MimeKit;
using SmtpGateway.Core;
using SmtpGateway.Infrastructure;
using Xunit;

namespace SmtpGateway.E2ETests;

/// <summary>
/// Reusable full-pipeline setup for the live E2E tests: a temp spool directory + SQLite queue, a
/// loopback-only <see cref="SmtpGatewayListener"/> (the same wiring the service uses), a MailKit
/// client that submits into it exactly like a legacy on-prem app, and an
/// <see cref="OutboundDeliveryWorker"/> bound to the real Microsoft 365 SMTP OAuth provider. This
/// factors out the pipeline plumbing that every content-variant test would otherwise copy-paste.
/// Each instance owns its own isolated temp root and cleans it up on <see cref="DisposeAsync"/>.
/// </summary>
internal sealed class LivePipelineHarness : IAsyncDisposable
{
    private const string SmtpScope = "https://outlook.office365.com/.default";

    private readonly string _root;
    private readonly string _databasePath;
    private readonly SmtpGatewayListener _listener;
    private readonly E2ECredentials _creds;

    public FileSpool Spool { get; }

    public SqliteQueueRepository Repository { get; }

    public int Port { get; }

    private LivePipelineHarness(
        string root,
        string databasePath,
        SmtpGatewayListener listener,
        FileSpool spool,
        SqliteQueueRepository repository,
        int port,
        E2ECredentials creds)
    {
        _root = root;
        _databasePath = databasePath;
        _listener = listener;
        Spool = spool;
        Repository = repository;
        Port = port;
        _creds = creds;
    }

    /// <summary>
    /// Creates and starts a harness. <paramref name="maxMessageSizeBytes"/> overrides the listener's
    /// message-size cap (used by the oversized-guard test); left null it keeps the product default.
    /// </summary>
    public static async Task<LivePipelineHarness> StartAsync(E2ECredentials creds, int? maxMessageSizeBytes = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "SmtpGateway.E2E.Pipeline", Guid.NewGuid().ToString("N"));
        var spoolDirectory = Path.Combine(root, "spool");
        var databasePath = Path.Combine(root, "queue.db");
        Directory.CreateDirectory(root);

        var spool = new FileSpool(spoolDirectory);
        var repository = new SqliteQueueRepository(databasePath);
        var port = GetFreeLoopbackPort();
        var options = new SmtpGatewayOptions
        {
            BindEndpoints = [new IPEndPoint(IPAddress.Loopback, port)],
            MaxMessageSizeBytes = maxMessageSizeBytes ?? SmtpGatewayOptions.DefaultMaxMessageSizeBytes,
        };

        var listener = new SmtpGatewayListener(options, spool, repository);
        await listener.StartAsync();

        return new LivePipelineHarness(root, databasePath, listener, spool, repository, port, creds);
    }

    /// <summary>Submits <paramref name="message"/> into the loopback gateway over plaintext SMTP.</summary>
    public async Task SendAsync(MimeMessage message, CancellationToken ct)
    {
        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", Port, SecureSocketOptions.None, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }

    /// <summary>
    /// Delivers the single queued item to the real tenant via the M365 SMTP OAuth provider and
    /// returns the reloaded queue item. Asserts that exactly one item was queued and that the
    /// worker processed it; the caller asserts the final status and per-recipient outcomes.
    /// </summary>
    public async Task<QueueItem> DeliverSingleAsync(CancellationToken ct)
    {
        var queued = Assert.Single(await Repository.ListAsync(ct));

        var worker = CreateWorker();
        var processed = await worker.ProcessNextAsync(ct);
        Assert.True(processed, "Expected the outbound worker to lease and process the queued item.");

        var reloaded = await Repository.GetByIdAsync(queued.Id, ct);
        Assert.NotNull(reloaded);
        return reloaded;
    }

    /// <summary>Reads the raw spooled MIME bytes for the given spool path.</summary>
    public Task<byte[]> ReadSpoolAsync(string mimePath, CancellationToken ct) => Spool.ReadAsync(mimePath, ct);

    private OutboundDeliveryWorker CreateWorker()
    {
        var tokenProvider = new MsalTokenProvider(_creds.TenantId, _creds.ClientId, _creds.ClientSecret, SmtpScope);
        var provider = new GenericSmtpProvider(new GenericSmtpProviderOptions
        {
            Host = "smtp.office365.com",
            Port = 587,
            TlsMode = SmtpTlsMode.StartTlsRequired,
            AuthMode = AuthMode.M365Oauth,
            Username = _creds.SenderMailbox,
            TokenProvider = tokenProvider,
        });

        return new OutboundDeliveryWorker(Repository, Spool, provider);
    }

    public async ValueTask DisposeAsync()
    {
        await _listener.DisposeAsync();

        using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _databasePath }.ConnectionString))
        {
            SqliteConnection.ClearPool(connection);
        }

        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of the isolated temp root; a lingering file handle must not
                // fail the test whose real work already completed.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
