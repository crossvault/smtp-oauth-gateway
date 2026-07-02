using SmtpGateway.Infrastructure;

namespace SmtpGateway.Service;

/// <summary>
/// Hosts the inbound SMTP listener for the lifetime of the service: starts
/// <see cref="SmtpGatewayListener"/> on startup and stops/disposes it cleanly on shutdown. A
/// fatal listener error is logged at Critical (reaching EventLog per the lifecycle category
/// filter), the process exit code is set non-zero, and the exception is rethrown so
/// <see cref="BackgroundServiceExceptionBehavior.StopHost"/> stops the host - no degraded mode.
/// </summary>
public sealed class InboundHostedService : BackgroundService
{
    private readonly GatewayOptions _options;
    private readonly FileSpool _spool;
    private readonly SqliteQueueRepository _repository;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _lifecycleLogger;

    public InboundHostedService(
        GatewayOptions options, FileSpool spool, SqliteQueueRepository repository, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _options = options;
        _spool = spool;
        _repository = repository;
        _loggerFactory = loggerFactory;
        _lifecycleLogger = loggerFactory.CreateLogger(LifecycleLog.CategoryName);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var smtpOptions = _options.Smtp.ToSmtpGatewayOptions();

        // Emit the network-reachability / open-relay / cleartext-AUTH warnings before starting, so an
        // operator who deliberately allowed a non-loopback bind sees them at startup. Loopback-only
        // binds (the default) produce no non-loopback endpoints and therefore no warnings.
        StartupBindingWarnings.Log(
            _lifecycleLogger,
            LoopbackEndpointValidator.GetNonLoopbackEndpoints(smtpOptions.BindEndpoints),
            smtpOptions.IsInboundAuthConfigured);

        await using var listener = new SmtpGatewayListener(
            smtpOptions, _spool, _repository, _options.MaxSpoolBytes, _loggerFactory);

        try
        {
            await listener.StartAsync().ConfigureAwait(false);
            _lifecycleLogger.LogInformation("Inbound SMTP listener started on {BindEndpoints}.", string.Join(", ", _options.Smtp.BindEndpoints));

            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _lifecycleLogger.LogCritical(ex, "Inbound SMTP listener failed fatally.");
            Environment.ExitCode = 1;
            throw;
        }
        finally
        {
            _lifecycleLogger.LogInformation("Inbound SMTP listener stopping.");
        }
    }
}
