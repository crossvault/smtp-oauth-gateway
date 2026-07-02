using SmtpGateway.Infrastructure;

namespace SmtpGateway.Service;

/// <summary>
/// Drives the outbound delivery loop: releases expired leases once at startup, then repeatedly
/// calls <see cref="OutboundDeliveryWorker.ProcessNextAsync"/> (looping immediately while there is
/// work, polling every <see cref="GatewayOptions.DeliveryPollInterval"/> once the queue is empty),
/// and periodically (per <see cref="GatewayOptions.TtlSweepInterval"/>, timing decision in
/// <see cref="TtlSweepPolicy"/>) both re-releases any lease that has expired mid-run (e.g. a prior
/// process was killed before a delivery attempt could complete) and runs the TTL-expiry sweep. A
/// fatal error is logged at Critical, the process exit code is set non-zero, and the exception is
/// rethrown so <see cref="BackgroundServiceExceptionBehavior.StopHost"/> stops the host - no
/// degraded mode.
/// </summary>
public sealed class OutboundDeliveryHostedService : BackgroundService
{
    private readonly GatewayOptions _options;
    private readonly SqliteQueueRepository _repository;
    private readonly OutboundDeliveryWorker _worker;
    private readonly ILogger _lifecycleLogger;

    public OutboundDeliveryHostedService(
        GatewayOptions options, SqliteQueueRepository repository, OutboundDeliveryWorker worker, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _options = options;
        _repository = repository;
        _worker = worker;
        _lifecycleLogger = loggerFactory.CreateLogger(LifecycleLog.CategoryName);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var released = await _repository.ReleaseExpiredLeasesAsync(stoppingToken).ConfigureAwait(false);
            _lifecycleLogger.LogInformation(
                "Outbound delivery worker starting; released {ReleasedCount} expired lease(s).", released);

            var lastSweepUtc = DateTimeOffset.UtcNow;

            while (!stoppingToken.IsCancellationRequested)
            {
                var processed = await _worker.ProcessNextAsync(stoppingToken).ConfigureAwait(false);

                var now = DateTimeOffset.UtcNow;
                if (TtlSweepPolicy.IsDue(lastSweepUtc, now, _options.TtlSweepInterval))
                {
                    // Re-run lease recovery on the same cadence as the TTL sweep, not just once at
                    // startup: a crash or cancelled in-flight delivery can strand an item as
                    // 'Leased' with a still-future LeaseExpiryUtc, and TryLeaseNextAsync never
                    // reclaims 'Leased' rows, so without this periodic sweep the item would sit
                    // undeliverable until TTL expiry silently drops it.
                    await _repository.ReleaseExpiredLeasesAsync(stoppingToken).ConfigureAwait(false);
                    await _repository.ExpireOverdueAsync(_options.EffectiveQueueTtl, stoppingToken).ConfigureAwait(false);
                    lastSweepUtc = now;
                }

                if (!processed)
                {
                    await Task.Delay(_options.DeliveryPollInterval, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _lifecycleLogger.LogCritical(ex, "Outbound delivery worker failed fatally.");
            Environment.ExitCode = 1;
            throw;
        }
        finally
        {
            _lifecycleLogger.LogInformation("Outbound delivery worker stopping.");
        }
    }
}
