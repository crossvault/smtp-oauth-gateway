using System.Net;
using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MimeKit;
using SmtpGateway.Core;
using SmtpGateway.Infrastructure;
using SmtpGateway.Service;
using Xunit;

namespace SmtpGateway.IntegrationTests;

/// <summary>
/// End-to-end smoke test for the actual composition root in <c>SmtpGateway.Service</c>: builds
/// the real <see cref="IHost"/> (the same DI wiring as Program.cs) pointed at a temp spool
/// directory, a temp SQLite file, and a <see cref="GenericSmtpProvider"/> talking to a fake local
/// SMTP server, starts it, sends one message into the loopback inbound listener via MailKit,
/// waits for the outbound delivery loop's poll to pick it up and deliver it, asserts the queue
/// item ends up Sent, and stops the host cleanly.
/// </summary>
public sealed class ServiceHostSmokeTests : IDisposable
{
    private readonly string _root;
    private readonly string _spoolDirectory;
    private readonly string _databasePath;

    public ServiceHostSmokeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "SmtpGateway.ServiceHostSmokeTests", Guid.NewGuid().ToString("N"));
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
    public async Task Host_AcceptsAndDeliversAMessageEndToEnd_ThenShutsDownCleanly()
    {
        var ct = TestContext.Current.CancellationToken;

        using var fakeServer = new FakeSmtpServer();
        var inboundPort = GetFreeLoopbackPort();

        var builder = Host.CreateApplicationBuilder();
        builder.Environment.ContentRootPath = _root;

        var gatewayOptions = new GatewayOptions
        {
            Smtp = new SmtpInboundOptions { BindEndpoints = [$"127.0.0.1:{inboundPort}"] },
            SpoolDirectory = _spoolDirectory,
            QueueDatabasePath = _databasePath,
            DeliveryPollInterval = TimeSpan.FromMilliseconds(200),
            TtlSweepInterval = TimeSpan.FromMinutes(15),
            OutboundProvider = new OutboundProviderOptions
            {
                Provider = "GenericSmtp",
                GenericSmtp = new GenericSmtpSettings
                {
                    Host = "127.0.0.1",
                    Port = fakeServer.Port,
                    TlsMode = SmtpTlsMode.None,
                    AuthMode = AuthMode.None,
                },
            },
        };

        builder.Services.AddSingleton(gatewayOptions);
        builder.Services.AddSingleton(sp => new FileSpool(sp.GetRequiredService<GatewayOptions>().SpoolDirectory));
        builder.Services.AddSingleton(sp => new SqliteQueueRepository(sp.GetRequiredService<GatewayOptions>().QueueDatabasePath));
        builder.Services.AddSingleton(sp => OutboundProviderFactory.Create(sp.GetRequiredService<GatewayOptions>().OutboundProvider));
        builder.Services.AddSingleton(sp => new OutboundDeliveryWorker(
            sp.GetRequiredService<SqliteQueueRepository>(),
            sp.GetRequiredService<FileSpool>(),
            sp.GetRequiredService<IOutboundProvider>(),
            sp.GetRequiredService<GatewayOptions>().SenderRewriteAddress,
            sp.GetRequiredService<GatewayOptions>().LeaseDuration));
        builder.Services.AddHostedService<InboundHostedService>();
        builder.Services.AddHostedService<OutboundDeliveryHostedService>();

        using var host = builder.Build();
        await host.StartAsync(ct);

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
            message.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
            message.Subject = "Service host smoke test";
            message.Body = new TextPart("plain") { Text = "Hello from the service host smoke test." };

            using var client = new SmtpClient();
            await client.ConnectAsync("127.0.0.1", inboundPort, SecureSocketOptions.None, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);

            var repository = host.Services.GetRequiredService<SqliteQueueRepository>();

            QueueItem? item = null;
            for (var i = 0; i < 100; i++)
            {
                var items = await repository.ListAsync(ct);
                item = items.SingleOrDefault();
                if (item is { Status: QueueItemStatus.Sent })
                {
                    break;
                }

                await Task.Delay(100, ct);
            }

            Assert.NotNull(item);
            Assert.Equal(QueueItemStatus.Sent, item!.Status);
            Assert.All(item.Recipients, r => Assert.Equal(RecipientStatus.Sent, r.Status));
        }
        finally
        {
            await host.StopAsync(ct);
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
