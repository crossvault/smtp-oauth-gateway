using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Storage;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// Enforces a per-mail recipient count limit. The running count is kept in
/// <see cref="ISessionContext.Properties"/> (reset on every MAIL FROM), since <see cref="IMailbox"/>
/// itself carries no envelope-wide state; recipients beyond the limit are rejected one at a
/// time rather than failing the whole transaction.
/// </summary>
public sealed class RecipientLimitMailboxFilter : MailboxFilter
{
    private const string RecipientCountPropertyKey = "SmtpGateway.RecipientCount";

    private readonly int _maxRecipients;
    private readonly ILogger<RecipientLimitMailboxFilter> _logger;

    public RecipientLimitMailboxFilter(int maxRecipients, ILogger<RecipientLimitMailboxFilter>? logger = null)
    {
        _maxRecipients = maxRecipients;
        _logger = logger ?? NullLogger<RecipientLimitMailboxFilter>.Instance;
    }

    public override Task<bool> CanAcceptFromAsync(
        ISessionContext context, IMailbox from, int size, CancellationToken cancellationToken)
    {
        context.Properties[RecipientCountPropertyKey] = 0;
        return Task.FromResult(true);
    }

    public override Task<bool> CanDeliverToAsync(
        ISessionContext context, IMailbox to, IMailbox from, CancellationToken cancellationToken)
    {
        var count = context.Properties.TryGetValue(RecipientCountPropertyKey, out var existing) && existing is int value
            ? value
            : 0;
        count++;
        context.Properties[RecipientCountPropertyKey] = count;

        var accepted = count <= _maxRecipients;
        if (!accepted)
        {
            _logger.LogWarning(
                "Inbound recipient rejected: recipient limit exceeded ({RecipientCount} > {MaxRecipients}).",
                count,
                _maxRecipients);
        }

        return Task.FromResult(accepted);
    }
}
