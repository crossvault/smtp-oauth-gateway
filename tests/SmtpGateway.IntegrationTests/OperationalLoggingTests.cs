using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MimeKit;
using SmtpGateway.Core;
using SmtpGateway.Infrastructure;
using SmtpGateway.Service;
using Xunit;

namespace SmtpGateway.IntegrationTests;

/// <summary>
/// Exercises a full accept-and-deliver flow through the real composition root (same DI wiring as
/// <see cref="ServiceHostSmokeTests"/>) with a capturing <see cref="ILoggerProvider"/> attached,
/// then asserts the new structured operational logging (added across
/// <see cref="SpoolingMessageStore"/>, <see cref="OutboundDeliveryWorker"/>, and
/// <see cref="RecipientLimitMailboxFilter"/>) references the queue item id but never leaks an
/// email address (proxied by a cheap but effective '@' scan of every message and structured
/// property value) or the secret/password configured on the outbound provider.
/// </summary>
public sealed class OperationalLoggingTests : IDisposable
{
    private const string SecretPassword = "Sup3rSecretPassw0rd!";
    private const string SecretUsername = "relay-service-account";

    private readonly string _root;
    private readonly string _spoolDirectory;
    private readonly string _databasePath;

    public OperationalLoggingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "SmtpGateway.OperationalLoggingTests", Guid.NewGuid().ToString("N"));
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
    public async Task Host_AcceptsAndDeliversAMessage_LogsReferenceQueueItemIdButNeverLeakPiiOrSecrets()
    {
        var ct = TestContext.Current.CancellationToken;

        using var fakeServer = new FakeSmtpServer();
        var inboundPort = GetFreeLoopbackPort();
        var capturingProvider = new CapturingLoggerProvider();

        var builder = Host.CreateApplicationBuilder();
        builder.Environment.ContentRootPath = _root;
        builder.Logging.AddProvider(capturingProvider);

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
                    // Not used by AuthMode.None, but present so the test can assert this secret
                    // never leaks into a log entry regardless of where it's configured.
                    Username = SecretUsername,
                    Password = SecretPassword,
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
            sp.GetRequiredService<GatewayOptions>().LeaseDuration,
            rateLimiter: null,
            utcNowProvider: null,
            logger: sp.GetRequiredService<ILogger<OutboundDeliveryWorker>>()));
        builder.Services.AddHostedService<InboundHostedService>();
        builder.Services.AddHostedService<OutboundDeliveryHostedService>();

        using var host = builder.Build();
        await host.StartAsync(ct);

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
            message.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
            message.Subject = "Operational logging test";
            message.Body = new TextPart("plain") { Text = "Hello from the operational logging test." };

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

            var entries = capturingProvider.Entries.ToList();
            Assert.NotEmpty(entries);

            var queueItemIdText = item.Id.ToString();
            Assert.Contains(entries, entry => ReferencesValue(entry, queueItemIdText));

            foreach (var entry in entries)
            {
                Assert.DoesNotContain('@', entry.Message);
                Assert.False(ReferencesValue(entry, SecretPassword), "A log entry leaked the configured secret password.");
                Assert.False(ReferencesValue(entry, SecretUsername), "A log entry leaked the configured secret username.");

                foreach (var property in entry.Properties)
                {
                    var text = property.Value?.ToString();
                    if (text is not null)
                    {
                        Assert.DoesNotContain('@', text);
                    }
                }
            }
        }
        finally
        {
            await host.StopAsync(ct);
        }
    }

    private static bool ReferencesValue(CapturedLogEntry entry, string value) =>
        entry.Message.Contains(value, StringComparison.Ordinal) ||
        entry.Properties.Any(p => string.Equals(p.Value?.ToString(), value, StringComparison.Ordinal));

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed record CapturedLogEntry(string Category, LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> Properties);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<CapturedLogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string categoryName, ConcurrentQueue<CapturedLogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var properties = (state as IEnumerable<KeyValuePair<string, object?>>)?.ToList()
                    ?? new List<KeyValuePair<string, object?>>();
                entries.Enqueue(new CapturedLogEntry(categoryName, logLevel, formatter(state, exception), properties));
            }
        }
    }
}
