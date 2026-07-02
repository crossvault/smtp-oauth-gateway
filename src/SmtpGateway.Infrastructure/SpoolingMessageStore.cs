using System.Buffers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmtpGateway.Core;
using SmtpServer;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// On a completed DATA command: writes the raw MIME to the file spool (temp-write, flush,
/// atomic rename), then enqueues a <see cref="QueueItem"/> in the SQLite queue - in that
/// order, spool commit before queue commit. The 250 OK is only returned once both have
/// durably committed. If either step fails, a non-success <see cref="SmtpResponse"/> is
/// returned so the client sees the transaction as rejected and can retry the whole thing;
/// nothing is partially committed as "accepted".
/// </summary>
public sealed class SpoolingMessageStore : MessageStore
{
    private readonly FileSpool _spool;
    private readonly SqliteQueueRepository _repository;
    private readonly int _maxMessageSizeBytes;
    private readonly long? _maxSpoolBytes;
    private readonly ILogger<SpoolingMessageStore> _logger;

    /// <summary>
    /// Serializes the quota-check-through-enqueue critical section so the disk-quota gate cannot be
    /// overshot by concurrent inbound DATA sessions that each read the same committed byte total,
    /// all pass the check, then all write. Only created when a quota is configured, so the
    /// default-unlimited path stays completely lock-free. The Service process is the only writer to
    /// the spool/queue (the TUI never calls <see cref="SaveAsync"/>), so this single in-process gate
    /// closes the race without any cross-process machinery.
    /// </summary>
    private readonly SemaphoreSlim? _quotaGate;

    public SpoolingMessageStore(
        FileSpool spool,
        SqliteQueueRepository repository,
        int maxMessageSizeBytes,
        long? maxSpoolBytes = null,
        ILogger<SpoolingMessageStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(repository);

        _spool = spool;
        _repository = repository;
        _maxMessageSizeBytes = maxMessageSizeBytes;
        _maxSpoolBytes = maxSpoolBytes;
        _logger = logger ?? NullLogger<SpoolingMessageStore>.Instance;
        _quotaGate = maxSpoolBytes.HasValue ? new SemaphoreSlim(1, 1) : null;
    }

    public override async Task<SmtpResponse> SaveAsync(
        ISessionContext context,
        IMessageTransaction transaction,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (buffer.Length > _maxMessageSizeBytes)
        {
            _logger.LogWarning(
                "Inbound message rejected: size limit exceeded ({SizeBytes} bytes, limit {MaxMessageSizeBytes} bytes).",
                buffer.Length,
                _maxMessageSizeBytes);
            return SmtpResponse.SizeLimitExceeded;
        }

        // When a quota is configured, hold the gate across the whole check-then-write-then-enqueue
        // section so a concurrent session cannot read the same committed byte total, pass the same
        // check, and overshoot the cap. Acquired before the read; released in finally after the
        // enqueue commits (so the next waiter's total already includes this message) or on any
        // early rejection/failure. Default-unlimited (_quotaGate == null) stays lock-free.
        if (_quotaGate is not null)
        {
            await _quotaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (_maxSpoolBytes is long maxSpoolBytes)
            {
                var currentSpoolBytes = await _repository.GetTotalSpoolBytesAsync(cancellationToken).ConfigureAwait(false);
                if (currentSpoolBytes + buffer.Length > maxSpoolBytes)
                {
                    // 452 (InsufficientStorage) is a temporary/retryable 4yz response: the legacy
                    // client should see a transient failure and retry later, not lose the message or
                    // have the gateway accept something it can't durably store safely. Nothing is
                    // written to the spool or the queue for a rejected message.
                    _logger.LogWarning(
                        "Inbound message rejected: spool quota exceeded ({CurrentSpoolBytes} + {MessageSizeBytes} bytes would exceed {MaxSpoolBytes} bytes).",
                        currentSpoolBytes,
                        buffer.Length,
                        maxSpoolBytes);
                    return new SmtpResponse(SmtpReplyCode.InsufficientStorage, "insufficient storage - spool quota exceeded");
                }
            }

            var rawMime = buffer.ToArray();
            var key = Guid.NewGuid();

            SpoolWriteResult spoolResult;
            try
            {
                spoolResult = await _spool.WriteAsync(key, rawMime, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Spool write did not complete durably - never acknowledge the transaction.
                _logger.LogError(ex, "Inbound message rejected: spool write failed for {QueueItemId}.", key);
                return SmtpResponse.TransactionFailed;
            }

            var item = BuildQueueItem(key, transaction, spoolResult);

            try
            {
                await _repository.EnqueueAsync(item, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Spool file is already on disk but the queue row failed to commit - still must
                // not ack as accepted; the spool file is simply orphaned, not a partial commit
                // of queue state.
                _logger.LogError(ex, "Inbound message rejected: queue enqueue failed for {QueueItemId}.", key);
                return SmtpResponse.TransactionFailed;
            }

            _logger.LogInformation(
                "Inbound message accepted: {QueueItemId}, {SizeBytes} bytes, {RecipientCount} recipients.",
                key,
                spoolResult.SizeBytes,
                item.Recipients.Count);

            return SmtpResponse.Ok;
        }
        finally
        {
            _quotaGate?.Release();
        }
    }

    private static QueueItem BuildQueueItem(Guid key, IMessageTransaction transaction, SpoolWriteResult spoolResult)
    {
        var recipients = transaction.To
            .Select(mailbox => new RecipientDelivery(FormatAddress(mailbox)))
            .ToList();
        var envelope = new Envelope(FormatAddress(transaction.From), recipients.Select(r => r.Address));
        var now = DateTimeOffset.UtcNow;

        return new QueueItem
        {
            Id = key,
            Envelope = envelope,
            Recipients = recipients,
            MimePath = spoolResult.Path,
            Hash = spoolResult.Hash,
            SizeBytes = spoolResult.SizeBytes,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Status = QueueItemStatus.Queued,
        };
    }

    private static string FormatAddress(SmtpServer.Mail.IMailbox mailbox) => $"{mailbox.User}@{mailbox.Host}";
}
