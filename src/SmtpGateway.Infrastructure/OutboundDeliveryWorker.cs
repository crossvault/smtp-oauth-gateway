using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using SmtpGateway.Core;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// Performs a single outbound delivery attempt cycle: lease one queue item, submit it to the
/// configured <see cref="IOutboundProvider"/>, and persist the per-recipient and item-level
/// outcome. Exposes exactly one testable unit of work (<see cref="ProcessNextAsync"/>) with no
/// internal loop/timer - a Worker Service host that calls this repeatedly belongs to Phase 4.
/// </summary>
public sealed class OutboundDeliveryWorker
{
    private const string LeaseOwner = "OutboundDeliveryWorker";
    private static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(5);

    private readonly SqliteQueueRepository _repository;
    private readonly FileSpool _spool;
    private readonly IOutboundProvider _provider;
    private readonly string? _rewriteSenderAddress;
    private readonly TimeSpan _leaseDuration;
    private readonly SlidingWindowRateLimiter? _rateLimiter;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ILogger<OutboundDeliveryWorker> _logger;

    public OutboundDeliveryWorker(
        SqliteQueueRepository repository,
        FileSpool spool,
        IOutboundProvider provider,
        string? rewriteSenderAddress = null,
        TimeSpan? leaseDuration = null,
        SlidingWindowRateLimiter? rateLimiter = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        ILogger<OutboundDeliveryWorker>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(provider);

        _repository = repository;
        _spool = spool;
        _provider = provider;
        _rewriteSenderAddress = rewriteSenderAddress;
        _leaseDuration = leaseDuration ?? DefaultLeaseDuration;
        _rateLimiter = rateLimiter;
        _utcNow = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
        _logger = logger ?? NullLogger<OutboundDeliveryWorker>.Instance;
    }

    /// <summary>
    /// Leases and processes one queue item. Returns <c>false</c> if the queue had no eligible
    /// item to claim, otherwise <c>true</c> once the attempt's outcome has been persisted.
    /// When an optional rate limit is configured and its rolling window is exhausted, this method
    /// also returns <c>false</c> without leasing anything - the caller's own idle poll/backoff
    /// (e.g. <c>OutboundDeliveryHostedService</c>'s <c>DeliveryPollInterval</c> wait) naturally
    /// defers the next attempt, so no additional timer or sleep is needed here.
    /// </summary>
    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        if (_rateLimiter is not null)
        {
            // Only spend a rate-limit token when there is actually an item to submit - checking
            // this first (read-only, no lease taken) means idle polling of an empty queue never
            // consumes the budget that a real message arriving moments later would need.
            var hasEligibleItem = await _repository.HasEligibleItemAsync(ct).ConfigureAwait(false);
            if (!hasEligibleItem)
            {
                return false;
            }

            if (!_rateLimiter.TryAcquire(_utcNow()))
            {
                return false;
            }
        }

        var item = await _repository.TryLeaseNextAsync(LeaseOwner, _leaseDuration, ct).ConfigureAwait(false);
        if (item is null)
        {
            return false;
        }

        // Per-item error isolation: one poison queue item (an unparseable sender/rewrite address,
        // a missing or unreadable spool file, malformed spooled MIME, a transient SQLite busy,
        // ...) must never escape this method and trip BackgroundServiceExceptionBehavior.StopHost,
        // which would kill the entire gateway - the inbound listener included - and then crash-loop
        // on restart. Genuinely fatal errors (startup lease-release/schema failures) live outside
        // this method in OutboundDeliveryHostedService and still stop the host by design.
        try
        {
            await ProcessLeasedItemAsync(item, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown - let the host's cooperative cancellation propagate.
            throw;
        }
        catch (Exception ex) when (ex is ParseException or FileNotFoundException or DirectoryNotFoundException)
        {
            // Deterministic, permanent data error: this item can never succeed on any future
            // attempt. Mark every recipient we would have attempted PermanentlyFailed so the item
            // derives to Poison (never re-leased) and surfaces to an operator, instead of being
            // retried on the backoff schedule forever.
            await FailItemPermanentlyAsync(item, ex, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // Transient/unexpected error (IO, SQLite busy beyond the retry timeout, ...): leave the
            // lease to expire so the periodic lease-recovery sweep re-queues the item for a later
            // attempt. Do not throw - a single item must not stop the host.
            _logger.LogError(
                ex,
                "Outbound delivery attempt failed transiently: {QueueItemId}; leaving lease to expire for a later retry.",
                item.Id);
            return true;
        }
    }

    /// <summary>
    /// The actual per-item delivery work for one already-leased item: read the spooled MIME,
    /// apply any sender rewrite, submit the still-pending recipients to the provider, and persist
    /// the per-recipient and item-level outcome. Any exception raised here is caught and classified
    /// by <see cref="ProcessNextAsync"/> so that a single bad item cannot stop the host.
    /// </summary>
    private async Task ProcessLeasedItemAsync(QueueItem item, CancellationToken ct)
    {
        var rawMime = await _spool.ReadAsync(item.MimePath, ct).ConfigureAwait(false);

        var effectiveMailFrom = SenderRewritePolicy.Resolve(item.Envelope.MailFrom, _rewriteSenderAddress);
        var effectiveRawMime = rawMime;
        if (!string.Equals(effectiveMailFrom, item.Envelope.MailFrom, StringComparison.Ordinal))
        {
            effectiveRawMime = await RewriteFromHeaderAsync(rawMime, effectiveMailFrom, ct).ConfigureAwait(false);
        }

        // Only (re)submit to recipients that still need an attempt. A re-leased PartiallySent
        // item already has some recipients Sent (and possibly others PermanentlyFailed) from an
        // earlier attempt - resubmitting those would resend duplicate mail or retry work that can
        // never succeed, so only Pending/Retryable recipients go to the provider this round.
        var recipientsToAttempt = item.Recipients
            .Where(r => r.Status is RecipientStatus.Pending or RecipientStatus.Retryable)
            .Select(r => r.Address)
            .ToList();

        _logger.LogInformation(
            "Outbound delivery attempt started: {QueueItemId}, {RecipientCount} recipients.",
            item.Id,
            recipientsToAttempt.Count);

        var effectiveEnvelope = new Envelope(effectiveMailFrom, recipientsToAttempt);
        var results = await _provider.Submit(effectiveEnvelope, effectiveRawMime, ct).ConfigureAwait(false);

        var anyRetryable = false;
        var successCount = 0;
        var retryableCount = 0;
        var permanentFailureCount = 0;
        TimeSpan? retryAfterHint = null;
        var updatedRecipients = new List<RecipientDelivery>(item.Recipients.Count);
        foreach (var recipient in item.Recipients)
        {
            if (!results.TryGetValue(recipient.Address, out var outcome))
            {
                updatedRecipients.Add(recipient);
                continue;
            }

            switch (outcome.Result)
            {
                case OutboundSubmitResult.Success:
                    successCount++;
                    await _repository.UpdateRecipientStatusAsync(
                        item.Id, recipient.Address, RecipientStatus.Sent, recipient.AttemptCount, null, ct)
                        .ConfigureAwait(false);
                    updatedRecipients.Add(recipient with { Status = RecipientStatus.Sent });
                    break;
                case OutboundSubmitResult.PermanentFailure:
                    permanentFailureCount++;
                    await _repository.UpdateRecipientStatusAsync(
                        item.Id,
                        recipient.Address,
                        RecipientStatus.PermanentlyFailed,
                        recipient.AttemptCount,
                        $"Outbound provider returned {outcome.Result}.",
                        ct)
                        .ConfigureAwait(false);
                    updatedRecipients.Add(recipient with { Status = RecipientStatus.PermanentlyFailed });
                    break;
                case OutboundSubmitResult.RetryableFailure:
                    anyRetryable = true;
                    retryableCount++;
                    if (outcome.RetryAfter is { } candidate && (retryAfterHint is null || candidate > retryAfterHint))
                    {
                        retryAfterHint = candidate;
                    }

                    await _repository.UpdateRecipientStatusAsync(
                        item.Id,
                        recipient.Address,
                        RecipientStatus.Retryable,
                        recipient.AttemptCount + 1,
                        $"Outbound provider returned {outcome.Result}.",
                        ct)
                        .ConfigureAwait(false);
                    updatedRecipients.Add(recipient with { Status = RecipientStatus.Retryable });
                    break;
                default:
                    updatedRecipients.Add(recipient);
                    break;
            }
        }

        _logger.LogInformation(
            "Outbound delivery attempt result: {QueueItemId}, {SuccessCount} succeeded, {RetryableFailureCount} retryable, {PermanentFailureCount} permanent failures.",
            item.Id,
            successCount,
            retryableCount,
            permanentFailureCount);

        var newStatus = QueueItemStatusResolver.Derive(updatedRecipients);
        if (newStatus == QueueItemStatus.Poison)
        {
            _logger.LogWarning(
                "Outbound delivery item transitioned: {QueueItemId}, new status {QueueItemStatus} - requires operator attention.",
                item.Id,
                newStatus);
        }
        else
        {
            _logger.LogInformation(
                "Outbound delivery item transitioned: {QueueItemId}, new status {QueueItemStatus}.",
                item.Id,
                newStatus);
        }

        if (anyRetryable)
        {
            var newAttemptCount = item.AttemptCount + 1;
            var delay = retryAfterHint ?? RetryPolicy.GetDelay(newAttemptCount);
            var nextAttemptUtc = _utcNow() + delay;
            await _repository.SetNextAttemptAsync(item.Id, newAttemptCount, nextAttemptUtc, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records a deterministic, permanent delivery failure for every recipient of
    /// <paramref name="item"/> that still awaited an attempt, so the item derives to
    /// <see cref="QueueItemStatus.Poison"/> (or terminal <see cref="QueueItemStatus.PartiallySent"/>
    /// if some recipients had already been Sent) and is never re-leased. Best-effort: if even the
    /// outcome write fails, the lease is left to expire rather than letting the failure escape and
    /// stop the host.
    /// </summary>
    private async Task FailItemPermanentlyAsync(QueueItem item, Exception error, CancellationToken ct)
    {
        _logger.LogError(
            error,
            "Outbound delivery attempt failed permanently: {QueueItemId}; marking attempted recipients permanently failed (requires operator attention).",
            item.Id);

        try
        {
            foreach (var recipient in item.Recipients)
            {
                if (recipient.Status is RecipientStatus.Pending or RecipientStatus.Retryable)
                {
                    await _repository.UpdateRecipientStatusAsync(
                        item.Id,
                        recipient.Address,
                        RecipientStatus.PermanentlyFailed,
                        recipient.AttemptCount,
                        $"Permanent delivery error: {error.GetType().Name}.",
                        ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception persistEx)
        {
            _logger.LogError(
                persistEx,
                "Failed to persist permanent-failure outcome for {QueueItemId}; leaving lease to expire for a later retry.",
                item.Id);
        }
    }

    /// <summary>
    /// Builds an in-memory copy of <paramref name="rawMime"/> with its "From:" header replaced by
    /// <paramref name="rewrittenFrom"/>. Never touches the original spool file or the queue row -
    /// only the bytes returned here, which are handed to the provider for this send attempt.
    /// </summary>
    private static async Task<byte[]> RewriteFromHeaderAsync(byte[] rawMime, string rewrittenFrom, CancellationToken ct)
    {
        using var inputStream = new MemoryStream(rawMime);
        var message = await MimeMessage.LoadAsync(inputStream, ct).ConfigureAwait(false);
        message.From.Clear();
        message.From.Add(MailboxAddress.Parse(rewrittenFrom));

        using var outputStream = new MemoryStream();
        await message.WriteToAsync(outputStream, ct).ConfigureAwait(false);
        return outputStream.ToArray();
    }
}
