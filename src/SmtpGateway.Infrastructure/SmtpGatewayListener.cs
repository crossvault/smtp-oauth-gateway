using Microsoft.Extensions.Logging;
using SmtpServer;
using SmtpServer.ComponentModel;
using SmtpServer.Storage;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// Wires the <c>SmtpServer</c> package up to the file spool and SQLite queue, and owns its
/// lifecycle. Bind endpoints are validated as loopback-only before the server is constructed.
/// </summary>
public sealed class SmtpGatewayListener : IAsyncDisposable
{
    private readonly SmtpServer.SmtpServer _server;
    private readonly CancellationTokenSource _cts = new();
    private Task? _runTask;

    public SmtpGatewayListener(
        SmtpGatewayOptions options,
        FileSpool spool,
        SqliteQueueRepository repository,
        long? maxSpoolBytes = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(repository);

        LoopbackEndpointValidator.ValidateLoopbackOnly(options.BindEndpoints);

        // SmtpGatewayListener isn't itself DI-resolved (it's constructed directly by
        // InboundHostedService), so it takes an ILoggerFactory - consistent with how the hosted
        // services already obtain per-category loggers - and hands each collaborator its own
        // typed logger; no logging happens here directly, only plumbing.
        var messageStoreLogger = loggerFactory?.CreateLogger<SpoolingMessageStore>();
        var recipientFilterLogger = loggerFactory?.CreateLogger<RecipientLimitMailboxFilter>();

        var serviceProvider = new ServiceProvider();
        serviceProvider.Add(
            (IMessageStore)new SpoolingMessageStore(
                spool, repository, options.MaxMessageSizeBytes, maxSpoolBytes, messageStoreLogger));
        serviceProvider.Add(
            (IMailboxFilter)new RecipientLimitMailboxFilter(options.MaxRecipients, recipientFilterLogger));

        var optionsBuilder = new SmtpServerOptionsBuilder()
            .ServerName(options.ServerName)
            .MaxMessageSize(options.MaxMessageSizeBytes, MaxMessageSizeHandling.Strict);

        foreach (var endpoint in options.BindEndpoints)
        {
            var boundEndpoint = endpoint;
            optionsBuilder = optionsBuilder.Endpoint(endpointBuilder => endpointBuilder
                .Endpoint(boundEndpoint)
                .AuthenticationRequired(false)
                .SessionTimeout(options.IdleTimeout));
        }

        _server = new SmtpServer.SmtpServer(optionsBuilder.Build(), serviceProvider);
    }

    public Task StartAsync()
    {
        _runTask = _server.StartAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _server.Shutdown();
        _cts.Cancel();

        if (_runTask is not null)
        {
            try
            {
                await _runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }
}
