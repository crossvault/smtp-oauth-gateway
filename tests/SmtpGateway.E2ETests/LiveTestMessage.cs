using MimeKit;
using SmtpGateway.Core;

namespace SmtpGateway.E2ETests;

/// <summary>
/// Builds the raw-MIME test messages (and matching envelopes) used by the live provider and
/// full-pipeline tests. Every message gets a distinctive GUID subject so concurrent or repeated
/// runs never collide in the recipient mailbox. No recipient/sender address is ever hardcoded -
/// they always come from <see cref="E2ECredentials"/> at runtime.
/// </summary>
internal static class LiveTestMessage
{
    private const string DefaultBodyText =
        "Automated SmtpGateway live end-to-end test message. Safe to ignore.";

    public static MimeMessage Build(E2ECredentials creds, string subjectTag)
    {
        var message = NewMessage(creds, subjectTag);
        message.To.Add(MailboxAddress.Parse(creds.RecipientMailbox));
        message.Body = new TextPart("plain") { Text = DefaultBodyText };
        return message;
    }

    public static (Envelope Envelope, byte[] RawMime) BuildRaw(E2ECredentials creds, string subjectTag) =>
        ToRaw(creds, Build(creds, subjectTag));

    /// <summary>Plain-text-only message: a single <c>text/plain</c> body, no HTML alternative.</summary>
    public static MimeMessage BuildTextOnly(E2ECredentials creds, string subjectTag, string bodyText)
    {
        var message = NewMessage(creds, subjectTag);
        message.To.Add(MailboxAddress.Parse(creds.RecipientMailbox));
        message.Body = new TextPart("plain") { Text = bodyText };
        return message;
    }

    /// <summary>HTML message: a <c>multipart/alternative</c> carrying both a text and an HTML part.</summary>
    public static MimeMessage BuildHtml(E2ECredentials creds, string subjectTag, string textBody, string htmlBody)
    {
        var message = NewMessage(creds, subjectTag);
        message.To.Add(MailboxAddress.Parse(creds.RecipientMailbox));
        var builder = new BodyBuilder { TextBody = textBody, HtmlBody = htmlBody };
        message.Body = builder.ToMessageBody();
        return message;
    }

    /// <summary>Message with a plain-text body plus one or more binary attachments.</summary>
    public static MimeMessage BuildWithAttachments(
        E2ECredentials creds,
        string subjectTag,
        string bodyText,
        IReadOnlyList<(string FileName, byte[] Content)> attachments)
    {
        var message = NewMessage(creds, subjectTag);
        message.To.Add(MailboxAddress.Parse(creds.RecipientMailbox));
        var builder = new BodyBuilder { TextBody = bodyText };
        foreach (var (fileName, content) in attachments)
        {
            builder.Attachments.Add(fileName, content);
        }

        message.Body = builder.ToMessageBody();
        return message;
    }

    /// <summary>
    /// Raw MIME + envelope for a single-attachment message, for the direct Graph sendMail path.
    /// </summary>
    public static (Envelope Envelope, byte[] RawMime) BuildRawWithAttachment(
        E2ECredentials creds, string subjectTag, string fileName, byte[] content) =>
        ToRaw(
            creds,
            BuildWithAttachments(
                creds,
                subjectTag,
                "Automated SmtpGateway live attachment test message. Safe to ignore.",
                [(fileName, content)]));

    /// <summary>
    /// Message addressed with distinct To, Cc, and Bcc recipients. MailKit derives the SMTP
    /// envelope (RCPT TO) from all three, but strips the Bcc header from the transmitted DATA.
    /// </summary>
    public static MimeMessage BuildCcBcc(
        E2ECredentials creds, string subjectTag, string to, string cc, string bcc)
    {
        var message = NewMessage(creds, subjectTag);
        message.To.Add(MailboxAddress.Parse(to));
        message.Cc.Add(MailboxAddress.Parse(cc));
        message.Bcc.Add(MailboxAddress.Parse(bcc));
        message.Body = new TextPart("plain")
        {
            Text = "Automated SmtpGateway live CC/BCC test message. Safe to ignore.",
        };
        return message;
    }

    private static MimeMessage NewMessage(E2ECredentials creds, string subjectTag)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(creds.SenderMailbox));
        message.Subject = $"SmtpGateway E2E {subjectTag} {Guid.NewGuid():N}";
        return message;
    }

    private static (Envelope Envelope, byte[] RawMime) ToRaw(E2ECredentials creds, MimeMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        var rawMime = stream.ToArray();
        var envelope = new Envelope(creds.SenderMailbox, [creds.RecipientMailbox]);
        return (envelope, rawMime);
    }
}
