using System.Net;
using System.Net.Sockets;
using SmtpGateway.Core;
using SmtpGateway.Infrastructure;
using Xunit;

namespace SmtpGateway.IntegrationTests;

/// <summary>
/// Inbound-SMTP protocol robustness / negative tests. These drive <see cref="SmtpGatewayListener"/>
/// (backed by a real <see cref="FileSpool"/> + <see cref="SqliteQueueRepository"/>) with a byte-level
/// <see cref="RawSmtpClient"/> so the exact protocol bytes are under test control - MailKit is too
/// well-behaved to send malformed or out-of-order commands. Assertions target the observable behavior
/// of the <c>SmtpServer</c> 11.1.0 package and, crucially, the durable side effects (spool files and
/// queue rows), not just the reply codes: a rejected transaction must leave nothing behind.
///
/// Reply-code notes reflect what SmtpServer 11.1.0 actually returns (observed, not assumed). Where the
/// package's specific code differs from a textbook expectation (e.g. it answers an out-of-order command
/// with 501 rather than 503), the tests assert the 4xx/5xx class boundary that actually matters and
/// document the exact observed code in a comment.
/// </summary>
public sealed class SmtpProtocolTests
{
    // 1 (unknown command) sent per bad-command session stays well under SmtpServer's per-session
    // retry threshold (it answers "N retry(ies) remaining"), so the session is never force-closed.

    [Fact]
    public async Task Ehlo_ReturnsMultilineCapabilities_WithSizeAdvertised()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await GatewayHarness.StartAsync(maxMessageSizeBytes: 2048);
        await using var client = await OpenSessionAsync(harness, ct);

        var reply = await client.CommandAsync("EHLO client.local", ct);

        // Observed: 250 multi-line - greeting line followed by PIPELINING / 8BITMIME / SMTPUTF8 / SIZE.
        Assert.Equal(250, reply.Code);
        Assert.True(reply.Lines.Count > 1, "EHLO must return a multi-line capability response.");

        // The multi-line reply was parsed to completion (final line had a space, not a hyphen).
        // SIZE must be advertised with the configured maximum so legacy clients can pre-check.
        Assert.Contains($"SIZE {harness.MaxMessageSizeBytes}", reply.Lines);
    }

    [Fact]
    public async Task Helo_LegacyClient_Returns250()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await GatewayHarness.StartAsync();
        await using var client = await OpenSessionAsync(harness, ct);

        // The gateway explicitly targets legacy software, so the bare-HELO path must work.
        var reply = await client.CommandAsync("HELO client.local", ct);

        // Observed: single-line 250 (no ESMTP capability list, unlike EHLO).
        Assert.Equal(250, reply.Code);
        Assert.Single(reply.Lines);
    }

    [Fact]
    public async Task UnknownCommand_IsRejected_SessionSurvivesAndStillDelivers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await GatewayHarness.StartAsync();
        await using var client = await OpenSessionAsync(harness, ct);
        await EhloAsync(client, ct);

        var garbage = await client.CommandAsync("FOO BAR", ct);

        // Observed: 502 "Unrecognized command". Assert the permanent-error class; the exact code is 502.
        Assert.Equal(5, garbage.CodeClass);

        // Session must survive a garbage command...
        var noop = await client.CommandAsync("NOOP", ct);
        Assert.Equal(250, noop.Code);

        // ...and a full, valid transaction on the same session must still be accepted end to end.
        var final = await SendMessageAsync(client, "sender@example.com", ["recipient@example.com"], ct);
        Assert.Equal(250, final.Code);

        var item = Assert.Single(await harness.Repository.ListAsync(ct));
        Assert.Equal(QueueItemStatus.Queued, item.Status);
        Assert.Equal(["recipient@example.com"], item.Envelope.Recipients);
    }

    [Fact]
    public async Task RcptToBeforeMailFrom_IsRejected_AsOutOfSequence()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await GatewayHarness.StartAsync();
        await using var client = await OpenSessionAsync(harness, ct);
        await EhloAsync(client, ct);

        var reply = await client.CommandAsync("RCPT TO:<recipient@example.com>", ct);

        // Observed: 501 "expected NOOP/RSET/QUIT/HELO/EHLO/MAIL" (SmtpServer answers an out-of-order
        // command with 501, not the textbook 503). A clean permanent rejection is what matters.
        Assert.Equal(5, reply.CodeClass);
        Assert.Empty(await harness.Repository.ListAsync(ct));
    }

    [Fact]
    public async Task DataBeforeRecipients_IsRejected_AsOutOfSequence()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await GatewayHarness.StartAsync();
        await using var client = await OpenSessionAsync(harness, ct);
        await EhloAsync(client, ct);

        // DATA with no transaction at all.
        var noTransaction = await client.CommandAsync("DATA", ct);
        // Observed: 501 "expected NOOP/RSET/QUIT/HELO/EHLO/MAIL".
        Assert.Equal(5, noTransaction.CodeClass);

        // DATA after MAIL FROM but before any RCPT TO.
        var mail = await client.CommandAsync("MAIL FROM:<sender@example.com>", ct);
        Assert.Equal(250, mail.Code);
        var noRecipients = await client.CommandAsync("DATA", ct);
        // Observed: 501 "expected NOOP/RSET/QUIT/RCPT" - the server refuses DATA without a recipient.
        Assert.Equal(5, noRecipients.CodeClass);

        Assert.Empty(await harness.Repository.ListAsync(ct));
    }

    [Fact]
    public async Task MalformedMailFrom_IsCleanlyRejected_NotAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await GatewayHarness.StartAsync();
        await using var client = await OpenSessionAsync(harness, ct);
        await EhloAsync(client, ct);

        var reply = await client.CommandAsync("MAIL FROM:no-brackets-garbage", ct);

        // The point is a clean error, not a hang and not a bogus 250. Observed: 502 "Unrecognized
        // command" - SmtpServer cannot parse the reverse-path so it treats the whole line as an
        // unknown command. Either way it is a permanent 5xx rejection, and nothing is accepted.
        Assert.Equal(5, reply.CodeClass);

        // The session is still usable afterwards (clean error, not a broken connection).
        var noop = await client.CommandAsync("NOOP", ct);
        Assert.Equal(250, noop.Code);

        Assert.Empty(await harness.Repository.ListAsync(ct));
    }

    [Fact]
    public async Task MailFromWithOversizeSizeParameter_IsRejectedAtMailFrom()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await GatewayHarness.StartAsync(maxMessageSizeBytes: 2048);
        await using var client = await OpenSessionAsync(harness, ct);
        await EhloAsync(client, ct);

        // Advertised SIZE lets a well-behaved client declare the message size up front; a declaration
        // over the cap must be refused immediately, before any DATA is transferred.
        var reply = await client.CommandAsync("MAIL FROM:<sender@example.com> SIZE=9999999", ct);

        // Observed: 552 "size limit exceeded" at the MAIL command.
        Assert.Equal(552, reply.Code);
        Assert.Empty(await harness.Repository.ListAsync(ct));
    }

    [Fact]
    public async Task RecipientLimitExceeded_FourthRejected_MessageWithThreeStillDelivered()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await GatewayHarness.StartAsync(maxRecipients: 3);
        await using var client = await OpenSessionAsync(harness, ct);
        await EhloAsync(client, ct);

        var mail = await client.CommandAsync("MAIL FROM:<sender@example.com>", ct);
        Assert.Equal(250, mail.Code);

        for (var i = 1; i <= 3; i++)
        {
            var accepted = await client.CommandAsync($"RCPT TO:<r{i}@example.com>", ct);
            Assert.Equal(250, accepted.Code);
        }

        // The 4th recipient is over the configured limit.
        var rejected = await client.CommandAsync("RCPT TO:<r4@example.com>", ct);
        // Observed: 550 "mailbox unavailable" from RecipientLimitMailboxFilter (per-recipient reject,
        // the transaction itself is not aborted).
        Assert.Equal(5, rejected.CodeClass);

        // The message with its 3 accepted recipients must still be deliverable.
        var body = await SendDataAsync(client, "Recipient limit test", ct);
        Assert.Equal(250, body.Code);

        var item = Assert.Single(await harness.Repository.ListAsync(ct));
        Assert.Equal(QueueItemStatus.Queued, item.Status);
        Assert.Equal(3, item.Recipients.Count);
        Assert.DoesNotContain("r4@example.com", item.Envelope.Recipients);
    }

    [Fact]
    public async Task OversizeData_IsRejected_NoSpoolFileNoQueueRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await GatewayHarness.StartAsync(maxMessageSizeBytes: 2048);
        await using var client = await OpenSessionAsync(harness, ct);
        await EhloAsync(client, ct);

        Assert.Equal(250, (await client.CommandAsync("MAIL FROM:<sender@example.com>", ct)).Code);
        Assert.Equal(250, (await client.CommandAsync("RCPT TO:<recipient@example.com>", ct)).Code);

        var dataPrompt = await client.CommandAsync("DATA", ct);
        Assert.Equal(354, dataPrompt.Code);

        // A body well over the 2048-byte cap, terminated normally with <CRLF>.<CRLF>.
        await client.SendRawAsync("Subject: oversize\r\n\r\n" + new string('x', 8000) + "\r\n.\r\n", ct);
        var reply = await client.ReadReplyAsync(ct);

        // Observed: 552 "message size exceeds fixed maximium message size" - SmtpServer's Strict
        // MaxMessageSize enforcement rejects during DATA (before SpoolingMessageStore's own guard,
        // which would also return 552). Permanent 5xx; nothing is stored.
        Assert.Equal(552, reply.Code);

        Assert.Empty(await harness.Repository.ListAsync(ct));
        Assert.Empty(Directory.EnumerateFiles(harness.SpoolDirectory));
    }

    [Fact]
    public async Task AbruptDisconnectMidData_LeavesNothingBehind_ListenerStaysHealthy()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await GatewayHarness.StartAsync();

        await using (var client = await OpenSessionAsync(harness, ct))
        {
            await EhloAsync(client, ct);
            Assert.Equal(250, (await client.CommandAsync("MAIL FROM:<sender@example.com>", ct)).Code);
            Assert.Equal(250, (await client.CommandAsync("RCPT TO:<recipient@example.com>", ct)).Code);
            Assert.Equal(354, (await client.CommandAsync("DATA", ct)).Code);

            // Send only part of the DATA payload - no terminating <CRLF>.<CRLF> - then rip the socket
            // away, simulating a crashed client mid-transfer.
            await client.SendRawAsync("Subject: interrupted\r\n\r\nfirst half of the body that never fin", ct);
            client.AbortConnection();
        }

        // The message store's SaveAsync only runs on a *completed* DATA, so an aborted transfer must
        // never produce a spool file (committed .eml or leftover .tmp) or a queue row.
        Assert.Empty(await harness.Repository.ListAsync(ct));
        Assert.Empty(Directory.EnumerateFiles(harness.SpoolDirectory));

        // The listener must keep serving: a following healthy session succeeds and is the only thing
        // that ends up in the queue / spool.
        await using (var client = await OpenSessionAsync(harness, ct))
        {
            await EhloAsync(client, ct);
            var reply = await SendMessageAsync(client, "sender2@example.com", ["recipient2@example.com"], ct);
            Assert.Equal(250, reply.Code);
        }

        var item = Assert.Single(await harness.Repository.ListAsync(ct));
        Assert.Equal(QueueItemStatus.Queued, item.Status);
        Assert.Equal(["recipient2@example.com"], item.Envelope.Recipients);
        Assert.True(File.Exists(item.MimePath));

        // Exactly one committed spool file, and no stray temp files from the aborted transfer.
        Assert.Single(Directory.EnumerateFiles(harness.SpoolDirectory, "*.eml"));
        Assert.Empty(Directory.EnumerateFiles(harness.SpoolDirectory, "*.tmp"));
    }

    [Fact]
    public async Task Rset_ClearsTransaction_ThenQuitClosesCleanly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await GatewayHarness.StartAsync();
        await using var client = await OpenSessionAsync(harness, ct);
        await EhloAsync(client, ct);

        Assert.Equal(250, (await client.CommandAsync("MAIL FROM:<sender@example.com>", ct)).Code);
        Assert.Equal(250, (await client.CommandAsync("RCPT TO:<recipient@example.com>", ct)).Code);

        var rset = await client.CommandAsync("RSET", ct);
        Assert.Equal(250, rset.Code);

        // After RSET the transaction is gone: DATA is out of sequence again until a fresh MAIL/RCPT.
        var dataAfterRset = await client.CommandAsync("DATA", ct);
        // Observed: 501 "expected NOOP/RSET/QUIT/HELO/EHLO/MAIL".
        Assert.Equal(5, dataAfterRset.CodeClass);

        // A fresh transaction after the RSET works normally...
        var reply = await SendMessageAsync(client, "sender@example.com", ["recipient@example.com"], ct);
        Assert.Equal(250, reply.Code);

        // ...and QUIT closes the session cleanly.
        var quit = await client.CommandAsync("QUIT", ct);
        Assert.Equal(221, quit.Code);

        // Only the one post-RSET message was committed (the RSET-discarded attempt left nothing).
        Assert.Single(await harness.Repository.ListAsync(ct));
    }

    [Fact]
    public async Task AcrossManyNegativeSessions_OnlyExpectedPositiveRowsPersist()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await GatewayHarness.StartAsync(maxRecipients: 3, maxMessageSizeBytes: 2048);

        // --- Positive session A: a plain, single-recipient message. ---
        await RunSessionAsync(harness, ct, async client =>
        {
            await EhloAsync(client, ct);
            Assert.Equal(250, (await SendMessageAsync(client, "a@example.com", ["a-rcpt@example.com"], ct)).Code);
        });

        // --- Negative session B: unknown command, then QUIT. ---
        await RunSessionAsync(harness, ct, async client =>
        {
            await EhloAsync(client, ct);
            Assert.Equal(5, (await client.CommandAsync("FOO BAR", ct)).CodeClass);
            await client.CommandAsync("QUIT", ct);
        });

        // --- Negative session C: RCPT before MAIL. ---
        await RunSessionAsync(harness, ct, async client =>
        {
            await EhloAsync(client, ct);
            Assert.Equal(5, (await client.CommandAsync("RCPT TO:<x@example.com>", ct)).CodeClass);
        });

        // --- Negative session D: malformed MAIL FROM. ---
        await RunSessionAsync(harness, ct, async client =>
        {
            await EhloAsync(client, ct);
            Assert.Equal(5, (await client.CommandAsync("MAIL FROM:garbage-no-brackets", ct)).CodeClass);
        });

        // --- Negative session E: oversize DATA. ---
        await RunSessionAsync(harness, ct, async client =>
        {
            await EhloAsync(client, ct);
            Assert.Equal(250, (await client.CommandAsync("MAIL FROM:<e@example.com>", ct)).Code);
            Assert.Equal(250, (await client.CommandAsync("RCPT TO:<e-rcpt@example.com>", ct)).Code);
            Assert.Equal(354, (await client.CommandAsync("DATA", ct)).Code);
            await client.SendRawAsync("Subject: big\r\n\r\n" + new string('x', 8000) + "\r\n.\r\n", ct);
            Assert.Equal(552, (await client.ReadReplyAsync(ct)).Code);
        });

        // --- Negative session F: abrupt disconnect mid-DATA. ---
        await RunSessionAsync(harness, ct, async client =>
        {
            await EhloAsync(client, ct);
            Assert.Equal(250, (await client.CommandAsync("MAIL FROM:<f@example.com>", ct)).Code);
            Assert.Equal(250, (await client.CommandAsync("RCPT TO:<f-rcpt@example.com>", ct)).Code);
            Assert.Equal(354, (await client.CommandAsync("DATA", ct)).Code);
            await client.SendRawAsync("Subject: dropped\r\n\r\nhalf a body", ct);
            client.AbortConnection();
        });

        // --- Positive session G: 4 recipients (limit 3) - 4th rejected, message with 3 committed. ---
        await RunSessionAsync(harness, ct, async client =>
        {
            await EhloAsync(client, ct);
            Assert.Equal(250, (await client.CommandAsync("MAIL FROM:<g@example.com>", ct)).Code);
            Assert.Equal(250, (await client.CommandAsync("RCPT TO:<g1@example.com>", ct)).Code);
            Assert.Equal(250, (await client.CommandAsync("RCPT TO:<g2@example.com>", ct)).Code);
            Assert.Equal(250, (await client.CommandAsync("RCPT TO:<g3@example.com>", ct)).Code);
            Assert.Equal(5, (await client.CommandAsync("RCPT TO:<g4@example.com>", ct)).CodeClass);
            Assert.Equal(250, (await SendDataAsync(client, "Three recipients", ct)).Code);
        });

        // --- Positive session H: RSET mid-transaction, then a clean send. ---
        await RunSessionAsync(harness, ct, async client =>
        {
            await EhloAsync(client, ct);
            Assert.Equal(250, (await client.CommandAsync("MAIL FROM:<h@example.com>", ct)).Code);
            Assert.Equal(250, (await client.CommandAsync("RCPT TO:<discarded@example.com>", ct)).Code);
            Assert.Equal(250, (await client.CommandAsync("RSET", ct)).Code);
            Assert.Equal(250, (await SendMessageAsync(client, "h@example.com", ["h-rcpt@example.com"], ct)).Code);
        });

        // --- Positive session I: a two-recipient message. ---
        await RunSessionAsync(harness, ct, async client =>
        {
            await EhloAsync(client, ct);
            Assert.Equal(250, (await SendMessageAsync(client, "i@example.com", ["i1@example.com", "i2@example.com"], ct)).Code);
        });

        // Exactly the four positive sessions (A, G, H, I) produced rows; every failure path committed
        // nothing. No partial commits, and no leftover temp files from the aborted/oversize transfers.
        var items = await harness.Repository.ListAsync(ct);
        Assert.Equal(4, items.Count);
        Assert.All(items, item => Assert.Equal(QueueItemStatus.Queued, item.Status));

        var recipientCounts = items.Select(i => i.Recipients.Count).OrderBy(n => n).ToArray();
        Assert.Equal([1, 1, 2, 3], recipientCounts);

        Assert.Equal(4, Directory.EnumerateFiles(harness.SpoolDirectory, "*.eml").Count());
        Assert.Empty(Directory.EnumerateFiles(harness.SpoolDirectory, "*.tmp"));
    }

    // --- helpers ------------------------------------------------------------------------------

    private static async Task<RawSmtpClient> OpenSessionAsync(GatewayHarness harness, CancellationToken ct)
    {
        var client = await harness.ConnectAsync(ct);
        var greeting = await client.ReadReplyAsync(ct);
        Assert.Equal(220, greeting.Code);
        return client;
    }

    private static async Task EhloAsync(RawSmtpClient client, CancellationToken ct)
    {
        var reply = await client.CommandAsync("EHLO client.local", ct);
        Assert.Equal(250, reply.Code);
    }

    /// <summary>Runs MAIL/RCPT.../DATA/body and returns the final reply to the terminating dot.</summary>
    private static async Task<RawSmtpReply> SendMessageAsync(
        RawSmtpClient client, string from, IReadOnlyList<string> recipients, CancellationToken ct)
    {
        var mail = await client.CommandAsync($"MAIL FROM:<{from}>", ct);
        Assert.Equal(250, mail.Code);
        foreach (var recipient in recipients)
        {
            var rcpt = await client.CommandAsync($"RCPT TO:<{recipient}>", ct);
            Assert.Equal(250, rcpt.Code);
        }

        return await SendDataAsync(client, "Protocol test message", ct);
    }

    /// <summary>Issues DATA and streams a small valid body, returning the reply to the terminating dot.</summary>
    private static async Task<RawSmtpReply> SendDataAsync(RawSmtpClient client, string subject, CancellationToken ct)
    {
        var dataPrompt = await client.CommandAsync("DATA", ct);
        Assert.Equal(354, dataPrompt.Code);
        await client.SendRawAsync($"Subject: {subject}\r\n\r\nHello from the protocol test.\r\n.\r\n", ct);
        return await client.ReadReplyAsync(ct);
    }

    private static async Task RunSessionAsync(
        GatewayHarness harness, CancellationToken ct, Func<RawSmtpClient, Task> session)
    {
        await using var client = await OpenSessionAsync(harness, ct);
        await session(client);
    }

    /// <summary>
    /// A running <see cref="SmtpGatewayListener"/> over a real file spool and SQLite queue in an
    /// isolated temp directory, on a free loopback port. Owns full cleanup (server shutdown, scoped
    /// connection-pool clear, temp-directory delete) on dispose.
    /// </summary>
    private sealed class GatewayHarness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _databasePath;
        private readonly SmtpGatewayListener _listener;

        private GatewayHarness(
            string root,
            string databasePath,
            string spoolDirectory,
            int port,
            int maxMessageSizeBytes,
            SmtpGatewayListener listener,
            SqliteQueueRepository repository)
        {
            _root = root;
            _databasePath = databasePath;
            SpoolDirectory = spoolDirectory;
            Port = port;
            MaxMessageSizeBytes = maxMessageSizeBytes;
            _listener = listener;
            Repository = repository;
        }

        public string SpoolDirectory { get; }

        public int Port { get; }

        public int MaxMessageSizeBytes { get; }

        public SqliteQueueRepository Repository { get; }

        public static async Task<GatewayHarness> StartAsync(
            int maxRecipients = SmtpGatewayOptions.DefaultMaxRecipients,
            int maxMessageSizeBytes = SmtpGatewayOptions.DefaultMaxMessageSizeBytes)
        {
            var root = Path.Combine(Path.GetTempPath(), "SmtpGateway.ProtocolTests", Guid.NewGuid().ToString("N"));
            var spoolDirectory = Path.Combine(root, "spool");
            var databasePath = Path.Combine(root, "queue.db");
            Directory.CreateDirectory(root);

            var spool = new FileSpool(spoolDirectory);
            var repository = new SqliteQueueRepository(databasePath);
            var port = GetFreeLoopbackPort();
            var options = new SmtpGatewayOptions
            {
                BindEndpoints = [new IPEndPoint(IPAddress.Loopback, port)],
                MaxRecipients = maxRecipients,
                MaxMessageSizeBytes = maxMessageSizeBytes,
            };

            var listener = new SmtpGatewayListener(options, spool, repository);
            await listener.StartAsync();

            return new GatewayHarness(
                root, databasePath, spoolDirectory, port, maxMessageSizeBytes, listener, repository);
        }

        public Task<RawSmtpClient> ConnectAsync(CancellationToken ct) => RawSmtpClient.ConnectAsync(Port, ct);

        public async ValueTask DisposeAsync()
        {
            await _listener.DisposeAsync();

            // Clear only this harness's own pooled connections (scoped by connection string), matching
            // the other listener tests, so it never races another test class's unrelated database.
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _databasePath }.ConnectionString))
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            }

            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static int GetFreeLoopbackPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
