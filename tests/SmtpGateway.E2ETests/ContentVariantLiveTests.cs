using System.Security.Cryptography;
using System.Text;
using MimeKit;
using SmtpGateway.Core;
using Xunit;

namespace SmtpGateway.E2ETests;

/// <summary>
/// Live content-fidelity tests: each drives a distinct MIME content variant through the FULL
/// pipeline (loopback listener -> SQLite queue -> <c>OutboundDeliveryWorker</c> -> real Microsoft
/// 365 SMTP OAuth provider) and asserts three things: that the queue item reaches
/// <see cref="QueueItemStatus.Sent"/>, that the spooled MIME (the gateway's source of truth)
/// preserved the content, and - the layer these tests add - that the content actually landed intact
/// in the recipient's real mailbox, read back over Microsoft Graph via
/// <see cref="GraphMailboxReader"/>. Each variant sends exactly ONE uniquely-GUID-subjected message
/// and then polls the recipient mailbox once for it, keeping live-tenant volume modest. The mailbox
/// assertions require the <c>Mail.Read</c> Graph application permission and self-skip (via
/// <see cref="LiveGraph.CreateMailboxReaderOrSkipAsync"/>) when it is absent; the send/spool checks
/// run regardless. All setup is shared via <see cref="LivePipelineHarness"/>, every test self-skips
/// when the live <c>.env</c> credentials are absent, and no address is ever hardcoded.
/// <para>
/// The received mails are intentionally left in the sandbox mailboxes: the reader holds only
/// <c>Mail.Read</c> (read-only), so it cannot delete them (that would need <c>Mail.ReadWrite</c>),
/// and the sandbox tolerates the modest accumulation.
/// </para>
/// </summary>
public sealed class ContentVariantLiveTests
{
    private const string NoCredentialsSkip = "Live O365 E2E credentials (.env) not present; skipping.";

    /// <summary>
    /// How long to poll a recipient mailbox for a just-sent message. Exchange internal delivery plus
    /// indexing typically completes in seconds but can take up to a couple of minutes.
    /// </summary>
    private static readonly TimeSpan MailboxPollTimeout = TimeSpan.FromMinutes(3);

    /// <summary>A more generous mailbox poll budget for the ~5 MB attachment variant.</summary>
    private static readonly TimeSpan LargeMailboxPollTimeout = TimeSpan.FromMinutes(6);

    [Fact]
    public async Task TextOnly_ThroughPipeline_ReachesSent_AndBodyRoundTrips()
    {
        var creds = E2ECredentials.Shared;
        Assert.SkipUnless(creds.Available, NoCredentialsSkip);

        var ct = TestContext.Current.CancellationToken;
        var bodyText = $"Automated SmtpGateway text-only variant. Marker {Guid.NewGuid():N}. Safe to ignore.";

        await using var harness = await LivePipelineHarness.StartAsync(creds);
        var message = LiveTestMessage.BuildTextOnly(creds, "text-only", bodyText);
        await harness.SendAsync(message, ct);

        var item = await harness.DeliverSingleAsync(ct);
        AssertSent(item);

        var spooled = await SpooledMime.LoadAsync(harness, item.MimePath, ct);
        Assert.Null(spooled.HtmlBody);
        Assert.Equal(bodyText, spooled.TextBody);

        // And prove the exact text actually landed in the recipient's real mailbox.
        using var reader = await LiveGraph.CreateMailboxReaderOrSkipAsync(creds, ct);
        var received = await ReceiveAsync(reader, creds.RecipientMailbox, message.Subject!, MailboxPollTimeout, ct);
        var receivedText = received.TextBody;
        Assert.NotNull(receivedText);
        Assert.Contains(bodyText, receivedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Html_ThroughPipeline_ReachesSent_AndKeepsBothAlternativeParts()
    {
        var creds = E2ECredentials.Shared;
        Assert.SkipUnless(creds.Available, NoCredentialsSkip);

        var ct = TestContext.Current.CancellationToken;
        var marker = Guid.NewGuid().ToString("N");
        var textBody = $"Plain alternative. Marker {marker}. Safe to ignore.";
        var htmlBody = $"<html><body><p>HTML alternative. Marker <strong>{marker}</strong>.</p></body></html>";

        await using var harness = await LivePipelineHarness.StartAsync(creds);
        var message = LiveTestMessage.BuildHtml(creds, "html", textBody, htmlBody);
        await harness.SendAsync(message, ct);

        var item = await harness.DeliverSingleAsync(ct);
        AssertSent(item);

        var spooled = await SpooledMime.LoadAsync(harness, item.MimePath, ct);
        Assert.IsType<MultipartAlternative>(spooled.Body);
        Assert.NotNull(spooled.TextBody);
        Assert.NotNull(spooled.HtmlBody);
        Assert.Contains(marker, spooled.TextBody);
        Assert.Contains(marker, spooled.HtmlBody);

        // Exchange legitimately rewrites the raw MIME structure, so assert on the DECODED HTML
        // alternative and its marker rather than on byte-identical MIME.
        using var reader = await LiveGraph.CreateMailboxReaderOrSkipAsync(creds, ct);
        var received = await ReceiveAsync(reader, creds.RecipientMailbox, message.Subject!, MailboxPollTimeout, ct);
        var receivedHtml = received.HtmlBody;
        Assert.NotNull(receivedHtml);
        Assert.Contains(marker, receivedHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SingleAttachment_ThroughPipeline_ReachesSent_AndAttachmentHashSurvives()
    {
        var creds = E2ECredentials.Shared;
        Assert.SkipUnless(creds.Available, NoCredentialsSkip);

        var ct = TestContext.Current.CancellationToken;
        var content = RandomNumberGenerator.GetBytes(4096);
        var fileName = $"payload-{Guid.NewGuid():N}.bin";
        var expectedHash = SpooledMime.Sha256Hex(content);

        await using var harness = await LivePipelineHarness.StartAsync(creds);
        var message = LiveTestMessage.BuildWithAttachments(
            creds, "single-attach", "Single attachment variant. Safe to ignore.", [(fileName, content)]);
        await harness.SendAsync(message, ct);

        var item = await harness.DeliverSingleAsync(ct);
        AssertSent(item);

        var spooled = await SpooledMime.LoadAsync(harness, item.MimePath, ct);
        AssertSingleAttachmentHash(spooled, fileName, expectedHash);

        // And prove the attachment bytes survived all the way into the recipient's mailbox.
        using var reader = await LiveGraph.CreateMailboxReaderOrSkipAsync(creds, ct);
        var received = await ReceiveAsync(reader, creds.RecipientMailbox, message.Subject!, MailboxPollTimeout, ct);
        AssertSingleAttachmentHash(received, fileName, expectedHash);
    }

    [Fact]
    public async Task MultipleAttachments_ThroughPipeline_ReachesSent_AndAllHashesSurvive()
    {
        var creds = E2ECredentials.Shared;
        Assert.SkipUnless(creds.Available, NoCredentialsSkip);

        var ct = TestContext.Current.CancellationToken;
        var batch = Guid.NewGuid().ToString("N");
        var attachments = new List<(string FileName, byte[] Content)>();
        var expectedHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < 10; i++)
        {
            // Distinct sizes as well as distinct random content, so a mixed-up mapping could not
            // accidentally still hash-match.
            var content = RandomNumberGenerator.GetBytes(512 + i);
            var fileName = $"part-{i:D2}-{batch}.bin";
            attachments.Add((fileName, content));
            expectedHashes[fileName] = SpooledMime.Sha256Hex(content);
        }

        await using var harness = await LivePipelineHarness.StartAsync(creds);
        var message = LiveTestMessage.BuildWithAttachments(
            creds, "multi-attach", "Ten attachments variant. Safe to ignore.", attachments);
        await harness.SendAsync(message, ct);

        var item = await harness.DeliverSingleAsync(ct);
        AssertSent(item);

        var spooled = await SpooledMime.LoadAsync(harness, item.MimePath, ct);
        AssertAllAttachmentHashes(spooled, expectedHashes);

        // And prove all ten attachments arrived intact in the recipient's mailbox.
        using var reader = await LiveGraph.CreateMailboxReaderOrSkipAsync(creds, ct);
        var received = await ReceiveAsync(reader, creds.RecipientMailbox, message.Subject!, MailboxPollTimeout, ct);
        AssertAllAttachmentHashes(received, expectedHashes);
    }

    [Fact]
    public async Task LargeAttachment_ThroughPipeline_ReachesSent_AndHashSurvives()
    {
        var creds = E2ECredentials.Shared;
        Assert.SkipUnless(creds.Available, NoCredentialsSkip);

        // ~5 MB of incompressible random bytes: well below the 25 MB gateway cap, but large enough
        // to genuinely exercise the size path and a real multi-second TLS upload to smtp.office365.com.
        var content = RandomNumberGenerator.GetBytes(5 * 1024 * 1024);
        var fileName = $"large-{Guid.NewGuid():N}.bin";
        var expectedHash = SpooledMime.Sha256Hex(content);

        // Generous ceiling: a 5 MB attachment base64-expands to ~6.7 MB on the wire, and the larger
        // message also takes longer to replicate into the mailbox before the read-back can find it.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(8));
        var ct = timeout.Token;

        await using var harness = await LivePipelineHarness.StartAsync(creds);
        var message = LiveTestMessage.BuildWithAttachments(
            creds, "large-attach", "Large attachment variant. Safe to ignore.", [(fileName, content)]);
        await harness.SendAsync(message, ct);

        var item = await harness.DeliverSingleAsync(ct);
        AssertSent(item);

        var spooled = await SpooledMime.LoadAsync(harness, item.MimePath, ct);
        AssertSingleAttachmentHash(spooled, fileName, expectedHash);

        // And prove the ~5 MB attachment survived byte-for-byte into the recipient's mailbox.
        using var reader = await LiveGraph.CreateMailboxReaderOrSkipAsync(creds, ct);
        var received = await ReceiveAsync(reader, creds.RecipientMailbox, message.Subject!, LargeMailboxPollTimeout, ct);
        AssertSingleAttachmentHash(received, fileName, expectedHash);
    }

    [Fact]
    public async Task OversizedMessage_IsRejectedAtSmtpTime_WhileSmallMessageStillAccepted()
    {
        var creds = E2ECredentials.Shared;
        // Purely local: no message is ever sent to M365 here (the rejection happens at the loopback
        // listener). Credentials are still required only so the From/To addresses are never hardcoded.
        Assert.SkipUnless(creds.Available, NoCredentialsSkip);

        var ct = TestContext.Current.CancellationToken;
        const int maxMessageSizeBytes = 4096;

        await using var harness = await LivePipelineHarness.StartAsync(creds, maxMessageSizeBytes);

        // A comfortably-small message is accepted and produces exactly one queue row.
        await harness.SendAsync(LiveTestMessage.BuildTextOnly(creds, "guard-small", "Small body. Safe to ignore."), ct);
        Assert.Single(await harness.Repository.ListAsync(ct));

        // A message just over the cap is rejected at SMTP time; the same still-running listener
        // produces no additional queue row (nothing was durably spooled or enqueued for it).
        var oversized = LiveTestMessage.BuildTextOnly(
            creds, "guard-oversized", new string('x', maxMessageSizeBytes * 4));
        await Assert.ThrowsAnyAsync<Exception>(() => harness.SendAsync(oversized, ct));

        Assert.Single(await harness.Repository.ListAsync(ct));
    }

    [Fact]
    public async Task CcAndBcc_ThroughPipeline_EnvelopeHasAllThree_ButBccHeaderIsStrippedFromSpool()
    {
        var creds = E2ECredentials.Shared;
        Assert.SkipUnless(creds.Available, NoCredentialsSkip);
        Assert.SkipUnless(
            creds.HasRecipientMailboxes,
            "SMTPGATEWAY_E2E_RECIPIENT_MAILBOXES absent or has fewer than 3 entries; skipping CC/BCC test.");

        var ct = TestContext.Current.CancellationToken;
        var to = creds.RecipientMailboxes[0];
        var cc = creds.RecipientMailboxes[1];
        var bcc = creds.RecipientMailboxes[2];

        await using var harness = await LivePipelineHarness.StartAsync(creds);
        var message = LiveTestMessage.BuildCcBcc(creds, "cc-bcc", to, cc, bcc);
        await harness.SendAsync(message, ct);

        var queued = Assert.Single(await harness.Repository.ListAsync(ct));

        // The BCC recipient arrives via RCPT TO, so the envelope must carry all three addresses.
        var expectedEnvelope = new HashSet<string>([to, cc, bcc], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expectedEnvelope, queued.Envelope.Recipients.ToHashSet(StringComparer.OrdinalIgnoreCase));

        // The spooled MIME keeps To and Cc headers, but MailKit strips the Bcc header client-side -
        // that omission is exactly what prevents BCC disclosure, so assert it actually holds.
        var spooled = await SpooledMime.LoadAsync(harness, queued.MimePath, ct);
        Assert.Contains(spooled.To.Mailboxes, m => string.Equals(m.Address, to, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(spooled.Cc.Mailboxes, m => string.Equals(m.Address, cc, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(spooled.Bcc);

        var headerBlock = ExtractHeaderBlock(await harness.ReadSpoolAsync(queued.MimePath, ct));
        Assert.Contains(to, headerBlock, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(cc, headerBlock, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(bcc, headerBlock, StringComparison.OrdinalIgnoreCase);

        // And it still delivers: every recipient (including the BCC) succeeds and the item is Sent.
        var item = await harness.DeliverSingleAsync(ct);
        AssertSent(item);
        Assert.Equal(3, item.Recipients.Count);

        // Full BCC contract, verified in the real mailboxes: the To recipient's delivered copy carries
        // the To and Cc headers but discloses the BCC address to no one; the BCC recipient nonetheless
        // received their own copy.
        using var reader = await LiveGraph.CreateMailboxReaderOrSkipAsync(creds, ct);

        var toRaw = await reader.WaitForRawMimeAsync(to, message.Subject!, MailboxPollTimeout, ct);
        var toReceived = await SpooledMime.ParseAsync(toRaw, ct);
        Assert.Contains(toReceived.To.Mailboxes, m => string.Equals(m.Address, to, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(toReceived.Cc.Mailboxes, m => string.Equals(m.Address, cc, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(toReceived.Bcc);

        // No BCC disclosure: the BCC address appears nowhere in the To recipient's received headers.
        var receivedHeaderBlock = ExtractHeaderBlock(toRaw);
        Assert.Contains(to, receivedHeaderBlock, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(cc, receivedHeaderBlock, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(bcc, receivedHeaderBlock, StringComparison.OrdinalIgnoreCase);

        // But the BCC copy WAS delivered: the blind-copy recipient has the message in their own mailbox.
        var bccArrived = await reader.MessageArrivedAsync(bcc, message.Subject!, MailboxPollTimeout, ct);
        Assert.True(bccArrived, "The BCC recipient's own mailbox never received the blind-copied message.");
    }

    private static void AssertSent(QueueItem item)
    {
        Assert.Equal(QueueItemStatus.Sent, item.Status);
        Assert.All(item.Recipients, recipient => Assert.Equal(RecipientStatus.Sent, recipient.Status));
    }

    /// <summary>Polls <paramref name="mailbox"/> for the uniquely-subjected message and parses its received MIME.</summary>
    private static async Task<MimeMessage> ReceiveAsync(
        GraphMailboxReader reader, string mailbox, string subject, TimeSpan timeout, CancellationToken ct)
    {
        var raw = await reader.WaitForRawMimeAsync(mailbox, subject, timeout, ct);
        return await SpooledMime.ParseAsync(raw, ct);
    }

    private static void AssertSingleAttachmentHash(MimeMessage message, string fileName, string expectedHash)
    {
        var hashes = SpooledMime.AttachmentHashesByFileName(message);
        var only = Assert.Single(hashes);
        Assert.Equal(fileName, only.Key);
        Assert.Equal(expectedHash, only.Value);
    }

    private static void AssertAllAttachmentHashes(MimeMessage message, IReadOnlyDictionary<string, string> expectedHashes)
    {
        var hashes = SpooledMime.AttachmentHashesByFileName(message);
        Assert.Equal(expectedHashes.Count, hashes.Count);
        foreach (var (fileName, expectedHash) in expectedHashes)
        {
            Assert.True(hashes.TryGetValue(fileName, out var actualHash), $"Attachment '{fileName}' missing.");
            Assert.Equal(expectedHash, actualHash);
        }
    }

    /// <summary>Returns the header portion (everything before the blank-line separator) as ASCII text.</summary>
    private static string ExtractHeaderBlock(byte[] rawMime)
    {
        var text = Encoding.ASCII.GetString(rawMime);
        var separator = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return separator >= 0 ? text[..separator] : text;
    }
}
