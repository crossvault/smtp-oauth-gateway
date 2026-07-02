using System.Net;
using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using SmtpGateway.Core;
using SmtpGateway.Infrastructure;
using Xunit;

namespace SmtpGateway.IntegrationTests;

public sealed class SmtpGatewayListenerTests : IDisposable
{
    private readonly string _root;
    private readonly string _spoolDirectory;
    private readonly string _databasePath;

    public SmtpGatewayListenerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "SmtpGateway.ListenerTests", Guid.NewGuid().ToString("N"));
        _spoolDirectory = Path.Combine(_root, "spool");
        _databasePath = Path.Combine(_root, "queue.db");
        Directory.CreateDirectory(_root);
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
    }

    [Fact]
    public async Task Message_IsAcceptedAndSpooledAndQueued()
    {
        var spool = new FileSpool(_spoolDirectory);
        var repository = new SqliteQueueRepository(_databasePath);
        var port = GetFreeLoopbackPort();
        var options = new SmtpGatewayOptions
        {
            BindEndpoints = [new IPEndPoint(IPAddress.Loopback, port)],
        };

        await using var listener = new SmtpGatewayListener(options, spool, repository);
        await listener.StartAsync();

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        message.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
        message.Subject = "Integration test";
        message.Body = new TextPart("plain") { Text = "Hello from the integration test." };

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None, TestContext.Current.CancellationToken);
        await client.SendAsync(message, TestContext.Current.CancellationToken);
        await client.DisconnectAsync(true, TestContext.Current.CancellationToken);

        var items = await repository.ListAsync(TestContext.Current.CancellationToken);
        var item = Assert.Single(items);
        Assert.Equal(QueueItemStatus.Queued, item.Status);
        Assert.Equal("sender@example.com", item.Envelope.MailFrom);
        Assert.Equal(["recipient@example.com"], item.Envelope.Recipients);

        Assert.True(File.Exists(item.MimePath));
        var spooledBytes = await File.ReadAllBytesAsync(item.MimePath, TestContext.Current.CancellationToken);
        using var stream = new MemoryStream(spooledBytes);
        var spooledMessage = await MimeMessage.LoadAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal("Integration test", spooledMessage.Subject);
    }

    [Fact]
    public async Task OversizedMessage_IsRejected()
    {
        var spool = new FileSpool(_spoolDirectory);
        var repository = new SqliteQueueRepository(_databasePath);
        var port = GetFreeLoopbackPort();
        var options = new SmtpGatewayOptions
        {
            BindEndpoints = [new IPEndPoint(IPAddress.Loopback, port)],
            MaxMessageSizeBytes = 1024,
        };

        await using var listener = new SmtpGatewayListener(options, spool, repository);
        await listener.StartAsync();

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        message.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
        message.Subject = "Oversized integration test";
        message.Body = new TextPart("plain") { Text = new string('x', 4096) };

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<Exception>(
            () => client.SendAsync(message, TestContext.Current.CancellationToken));

        var items = await repository.ListAsync(TestContext.Current.CancellationToken);
        Assert.Empty(items);
    }

    [Fact]
    public async Task NoSpoolQuotaConfigured_MessageIsAlwaysAccepted()
    {
        var spool = new FileSpool(_spoolDirectory);
        var repository = new SqliteQueueRepository(_databasePath);
        var port = GetFreeLoopbackPort();
        var options = new SmtpGatewayOptions
        {
            BindEndpoints = [new IPEndPoint(IPAddress.Loopback, port)],
        };

        // maxSpoolBytes intentionally omitted (null = unlimited): no backpressure check runs.
        await using var listener = new SmtpGatewayListener(options, spool, repository);
        await listener.StartAsync();

        await SendTestMessageAsync(port, bodySize: 4096);

        var items = await repository.ListAsync(TestContext.Current.CancellationToken);
        Assert.Single(items);
    }

    [Fact]
    public async Task SpoolQuotaConfigured_UnderLimit_MessageIsAcceptedAndSpooledAndQueued()
    {
        var spool = new FileSpool(_spoolDirectory);
        var repository = new SqliteQueueRepository(_databasePath);
        var port = GetFreeLoopbackPort();
        var options = new SmtpGatewayOptions
        {
            BindEndpoints = [new IPEndPoint(IPAddress.Loopback, port)],
        };

        await using var listener = new SmtpGatewayListener(options, spool, repository, maxSpoolBytes: 1_000_000);
        await listener.StartAsync();

        await SendTestMessageAsync(port, bodySize: 4096);

        var items = await repository.ListAsync(TestContext.Current.CancellationToken);
        var item = Assert.Single(items);
        Assert.Equal(QueueItemStatus.Queued, item.Status);
        Assert.True(File.Exists(item.MimePath));
    }

    [Fact]
    public async Task SpoolQuotaConfigured_WouldExceedLimit_MessageIsRejectedAndNothingIsWritten()
    {
        var spool = new FileSpool(_spoolDirectory);
        var repository = new SqliteQueueRepository(_databasePath);
        var port = GetFreeLoopbackPort();

        // A tiny quota that even a single small test message will exceed.
        var options = new SmtpGatewayOptions
        {
            BindEndpoints = [new IPEndPoint(IPAddress.Loopback, port)],
        };

        await using var listener = new SmtpGatewayListener(options, spool, repository, maxSpoolBytes: 10);
        await listener.StartAsync();

        var key = Guid.NewGuid();
        var expectedRejectedPath = spool.GetFinalPath(key);

        await Assert.ThrowsAnyAsync<Exception>(() => SendTestMessageAsync(port, bodySize: 4096));

        var items = await repository.ListAsync(TestContext.Current.CancellationToken);
        Assert.Empty(items);

        // No spool file for this rejected message should exist anywhere in the spool directory.
        Assert.Empty(Directory.EnumerateFiles(_spoolDirectory));
        Assert.False(File.Exists(expectedRejectedPath));
    }

    private static async Task SendTestMessageAsync(int port, int bodySize)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        message.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
        message.Subject = "Backpressure test";
        message.Body = new TextPart("plain") { Text = new string('x', bodySize) };

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None, TestContext.Current.CancellationToken);
        await client.SendAsync(message, TestContext.Current.CancellationToken);
        await client.DisconnectAsync(true, TestContext.Current.CancellationToken);
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
