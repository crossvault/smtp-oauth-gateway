using SmtpGateway.Core;

namespace SmtpGateway.Admin.Tui;

/// <summary>
/// The single source of truth for how the queue listing is filtered, shared by the
/// 'queue list' command and the interactive shell's queue browser so both apply exactly the
/// same rule: an explicit <paramref name="status"/> filter is honoured verbatim, but the
/// default (unfiltered) view hides Discarded items - queue history is never deleted, yet an
/// administrator-discarded item should not clutter the everyday view.
/// </summary>
public static class QueueListFilter
{
    public static IReadOnlyList<QueueItem> Filter(IReadOnlyList<QueueItem> items, QueueItemStatus? status)
    {
        ArgumentNullException.ThrowIfNull(items);

        return status is { } wanted
            ? items.Where(item => item.Status == wanted).ToList()
            : items.Where(item => item.Status != QueueItemStatus.Discarded).ToList();
    }
}
