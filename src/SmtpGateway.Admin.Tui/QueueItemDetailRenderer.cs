using System.Globalization;
using Spectre.Console;
using SmtpGateway.Core;

namespace SmtpGateway.Admin.Tui;

/// <summary>
/// Shared renderer for one queue item's full detail (envelope, timestamps, lease, spool metadata,
/// and the per-recipient table), used by both the 'queue show' command and the interactive shell's
/// queue browser so the two present identical information.
///
/// Envelope addresses, error text and spool paths are untrusted/variable strings that Spectre would
/// otherwise parse as console markup: an RFC-valid address literal like a@[10.0.0.1] contains '['
/// and would throw a malformed-markup exception at render, and a quoted local part could inject
/// color/hyperlink escapes into the admin terminal. <see cref="Markup.Escape"/> neutralizes the
/// metacharacters while preserving the stored value verbatim in the visible output.
/// </summary>
public static class QueueItemDetailRenderer
{
    public static void Write(QueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var details = new Table().HideHeaders().AddColumn("Field").AddColumn("Value");
        details.AddRow("Id", item.Id.ToString());
        details.AddRow("Status", item.Status.ToString());
        details.AddRow("Mail from", Markup.Escape(item.Envelope.MailFrom));
        details.AddRow("Recipients", Markup.Escape(string.Join(", ", item.Envelope.Recipients)));
        details.AddRow("Created (UTC)", item.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        details.AddRow("Updated (UTC)", item.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        details.AddRow("Next attempt (UTC)", item.NextAttemptUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "n/a");
        details.AddRow("Lease owner", Markup.Escape(item.LeaseOwner ?? "n/a"));
        details.AddRow("Lease expiry (UTC)", item.LeaseExpiryUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "n/a");
        details.AddRow("Attempt count", item.AttemptCount.ToString(CultureInfo.InvariantCulture));
        details.AddRow("Last error", Markup.Escape(item.LastError ?? "n/a"));
        details.AddRow("Spool path", Markup.Escape(item.MimePath));
        details.AddRow("Spool hash (SHA-256)", Markup.Escape(item.Hash));
        details.AddRow("Spool size (bytes)", item.SizeBytes.ToString("N0", CultureInfo.InvariantCulture));

        AnsiConsole.Write(details);

        var recipients = new Table();
        recipients.Title("Recipients");
        recipients.AddColumn("Address");
        recipients.AddColumn("Status");
        recipients.AddColumn(new TableColumn("Attempts").RightAligned());
        recipients.AddColumn("Last error");
        foreach (var recipient in item.Recipients)
        {
            recipients.AddRow(
                Markup.Escape(recipient.Address),
                recipient.Status.ToString(),
                recipient.AttemptCount.ToString(CultureInfo.InvariantCulture),
                Markup.Escape(recipient.LastError ?? "n/a"));
        }

        AnsiConsole.Write(recipients);
    }
}
