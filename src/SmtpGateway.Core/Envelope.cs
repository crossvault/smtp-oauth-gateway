namespace SmtpGateway.Core;

/// <summary>
/// The SMTP envelope for a single mail: the MAIL FROM address and the set of RCPT TO
/// recipients. Immutable once constructed.
/// </summary>
public sealed class Envelope
{
    public string MailFrom { get; }

    public IReadOnlyList<string> Recipients { get; }

    public Envelope(string mailFrom, IEnumerable<string> recipients)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mailFrom);
        ArgumentNullException.ThrowIfNull(recipients);

        var recipientList = recipients.ToList();
        if (recipientList.Count == 0)
        {
            throw new ArgumentException("Envelope must have at least one recipient.", nameof(recipients));
        }

        MailFrom = mailFrom;
        Recipients = recipientList.AsReadOnly();
    }
}
