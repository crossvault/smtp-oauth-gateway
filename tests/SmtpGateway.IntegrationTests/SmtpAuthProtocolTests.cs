using System.Net;
using System.Net.Sockets;
using System.Text;
using SmtpGateway.Core;
using SmtpGateway.Infrastructure;
using Xunit;

namespace SmtpGateway.IntegrationTests;

/// <summary>
/// Protocol-level tests for inbound SMTP AUTH, wired through a real <see cref="SmtpGatewayListener"/>
/// (backed by a real <see cref="FileSpool"/> + <see cref="SqliteQueueRepository"/>) with the byte-level
/// <see cref="RawSmtpClient"/>. When inbound credentials are configured, AUTH must be advertised,
/// required before MAIL FROM, and enforced (correct credentials submit; wrong credentials are rejected
/// 535-class and leave the session unable to submit). All binds are loopback - AUTH semantics are
/// endpoint-independent, and CI runners forbid/vary real LAN binds.
/// </summary>
public sealed class SmtpAuthProtocolTests
{
    private const string Username = "relay-user";
    private const string Password = "s3cr3t-p@ss";

    [Fact]
    public async Task Ehlo_WithCredentialsConfigured_AdvertisesAuth()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await AuthHarness.StartAsync(Username, Password);
        await using var client = await OpenSessionAsync(harness, ct);

        var reply = await client.CommandAsync("EHLO client.local", ct);

        Assert.Equal(250, reply.Code);
        // SmtpServer advertises "AUTH PLAIN LOGIN" (AllowUnsecureAuthentication is on for the
        // TLS-less inbound listener). Assert the AUTH capability is present.
        Assert.Contains(reply.Lines, line => line.StartsWith("AUTH", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MailFrom_BeforeAuth_IsRejected_AndNothingIsQueued()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await AuthHarness.StartAsync(Username, Password);
        await using var client = await OpenSessionAsync(harness, ct);
        await EhloAsync(client, ct);

        var reply = await client.CommandAsync("MAIL FROM:<sender@example.com>", ct);

        // Observed: 530 "Authentication required". Assert the permanent-error class at minimum.
        Assert.Equal(5, reply.CodeClass);
        Assert.Empty(await harness.Repository.ListAsync(ct));
    }

    [Fact]
    public async Task AuthLogin_CorrectCredentials_ThenFullTransactionSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await AuthHarness.StartAsync(Username, Password);
        await using var client = await OpenSessionAsync(harness, ct);
        await EhloAsync(client, ct);

        var authReply = await AuthLoginAsync(client, Username, Password, ct);
        Assert.Equal(235, authReply.Code);

        Assert.Equal(250, (await client.CommandAsync("MAIL FROM:<sender@example.com>", ct)).Code);
        Assert.Equal(250, (await client.CommandAsync("RCPT TO:<recipient@example.com>", ct)).Code);
        Assert.Equal(354, (await client.CommandAsync("DATA", ct)).Code);
        await client.SendRawAsync("Subject: authed\r\n\r\nHello from an authenticated session.\r\n.\r\n", ct);
        var final = await client.ReadReplyAsync(ct);
        Assert.Equal(250, final.Code);

        var item = Assert.Single(await harness.Repository.ListAsync(ct));
        Assert.Equal(QueueItemStatus.Queued, item.Status);
        Assert.Equal(["recipient@example.com"], item.Envelope.Recipients);
    }

    [Fact]
    public async Task AuthLogin_WrongPassword_IsRejected_AndSessionCannotSubmit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await AuthHarness.StartAsync(Username, Password);
        await using var client = await OpenSessionAsync(harness, ct);
        await EhloAsync(client, ct);

        var authReply = await AuthLoginAsync(client, Username, "wrong-password", ct);
        // Observed: 535 "Authentication unsuccessful" - the 535-class auth failure.
        Assert.Equal(535, authReply.Code);

        // Still unauthenticated: MAIL FROM must remain rejected and nothing may be submitted.
        var mail = await client.CommandAsync("MAIL FROM:<sender@example.com>", ct);
        Assert.Equal(5, mail.CodeClass);

        Assert.Empty(await harness.Repository.ListAsync(ct));
        Assert.Empty(Directory.EnumerateFiles(harness.SpoolDirectory));
    }

    // --- helpers ------------------------------------------------------------------------------

    private static async Task<RawSmtpClient> OpenSessionAsync(AuthHarness harness, CancellationToken ct)
    {
        var client = await harness.ConnectAsync(ct);
        var greeting = await client.ReadReplyAsync(ct);
        Assert.Equal(220, greeting.Code);
        return client;
    }

    private static async Task EhloAsync(RawSmtpClient client, CancellationToken ct)
    {
        var reply = await client.CommandAsync("EHLO client.local", ct);
        Assert.Equal(250, reply.Code);
    }

    /// <summary>Drives the multi-step AUTH LOGIN exchange and returns the final server reply.</summary>
    private static async Task<RawSmtpReply> AuthLoginAsync(
        RawSmtpClient client, string username, string password, CancellationToken ct)
    {
        var challenge = await client.CommandAsync("AUTH LOGIN", ct);
        Assert.Equal(334, challenge.Code);

        var userReply = await client.CommandAsync(Base64(username), ct);
        Assert.Equal(334, userReply.Code);

        return await client.CommandAsync(Base64(password), ct);
    }

    private static string Base64(string value) => Convert.ToBase64String(Encoding.ASCII.GetBytes(value));

    private sealed class AuthHarness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _databasePath;
        private readonly SmtpGatewayListener _listener;

        private AuthHarness(
            string root, string databasePath, string spoolDirectory, int port,
            SmtpGatewayListener listener, SqliteQueueRepository repository)
        {
            _root = root;
            _databasePath = databasePath;
            SpoolDirectory = spoolDirectory;
            Port = port;
            _listener = listener;
            Repository = repository;
        }

        public string SpoolDirectory { get; }

        public int Port { get; }

        public SqliteQueueRepository Repository { get; }

        public static async Task<AuthHarness> StartAsync(string? authUsername, string? authPassword)
        {
            var root = Path.Combine(Path.GetTempPath(), "SmtpGateway.AuthProtocolTests", Guid.NewGuid().ToString("N"));
            var spoolDirectory = Path.Combine(root, "spool");
            var databasePath = Path.Combine(root, "queue.db");
            Directory.CreateDirectory(root);

            var spool = new FileSpool(spoolDirectory);
            var repository = new SqliteQueueRepository(databasePath);
            var port = GetFreeLoopbackPort();
            var options = new SmtpGatewayOptions
            {
                BindEndpoints = [new IPEndPoint(IPAddress.Loopback, port)],
                AuthUsername = authUsername,
                AuthPassword = authPassword,
            };

            var listener = new SmtpGatewayListener(options, spool, repository);
            await listener.StartAsync();

            return new AuthHarness(root, databasePath, spoolDirectory, port, listener, repository);
        }

        public Task<RawSmtpClient> ConnectAsync(CancellationToken ct) => RawSmtpClient.ConnectAsync(Port, ct);

        public async ValueTask DisposeAsync()
        {
            await _listener.DisposeAsync();

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

        private static int GetFreeLoopbackPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
