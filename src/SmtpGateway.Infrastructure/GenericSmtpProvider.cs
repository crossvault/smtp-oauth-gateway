using System.Diagnostics;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using SmtpGateway.Core;
using Envelope = SmtpGateway.Core.Envelope;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// Outbound provider that relays mail to a generic (non-Graph) SMTP server via MailKit,
/// reporting a per-recipient <see cref="OutboundSubmitResult"/> for the mixed-success case
/// where some recipients are accepted and others are rejected.
/// </summary>
public sealed class GenericSmtpProvider : IOutboundProvider
{
    private readonly GenericSmtpProviderOptions _options;

    public GenericSmtpProvider(GenericSmtpProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.AuthMode == AuthMode.M365Oauth)
        {
            if (options.TokenProvider is null)
            {
                throw new ArgumentException(
                    $"{nameof(GenericSmtpProviderOptions.TokenProvider)} is required when {nameof(GenericSmtpProviderOptions.AuthMode)} is {AuthMode.M365Oauth}.",
                    nameof(options));
            }

            if (string.IsNullOrEmpty(options.Username))
            {
                throw new ArgumentException(
                    $"{nameof(GenericSmtpProviderOptions.Username)} is required when {nameof(GenericSmtpProviderOptions.AuthMode)} is {AuthMode.M365Oauth}.",
                    nameof(options));
            }
        }

        _options = options;
    }

    public async Task<IReadOnlyDictionary<string, SubmitOutcome>> Submit(
        Envelope envelope, byte[] rawMime, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(rawMime);

        // Keyed by either the caller's original recipient string or MailKit's normalized
        // MailboxAddress.Address (both are used by different code paths below); the final
        // result-building loop checks both forms when mapping back to the caller's addresses.
        var trackedResults = new Dictionary<string, OutboundSubmitResult>(StringComparer.OrdinalIgnoreCase);

        using var client = new RecipientTrackingSmtpClient(trackedResults);

        // Parse recipients tolerantly: SmtpServer will have accepted (and stored) addresses that
        // MimeKit cannot parse - e.g. a quoted local part `"weird stuff"@x` is stored quote-stripped
        // as `weird stuff@x`, which MailboxAddress.Parse rejects. Such a recipient is a
        // deterministic, permanent, per-recipient condition: record PermanentFailure for it and
        // exclude it from the send, so the remaining (parseable) recipients are still delivered and
        // the IOutboundProvider contract of one outcome per recipient still holds - rather than
        // throwing out of Submit and taking down deliverable recipients (or the host) with it.
        var sendable = new List<(string Original, MailboxAddress Parsed)>();
        foreach (var address in envelope.Recipients)
        {
            if (MailboxAddress.TryParse(address, out var parsed))
            {
                sendable.Add((address, parsed));
            }
            else
            {
                trackedResults[address] = OutboundSubmitResult.PermanentFailure;
            }
        }

        // Parse MAIL FROM tolerantly too. An unparseable sender is deterministic and permanent -
        // most notably the null return-path `MAIL FROM:<>` (bounces/DSNs/monitoring pings), which
        // SpoolingMessageStore stores as the literal "@". Sending is impossible without a valid
        // reverse-path, so fail every recipient permanently (never RetryableFailure, which would
        // otherwise burn the full multi-day backoff schedule before a silent TTL expiry) and do
        // not open a connection at all.
        if (!MailboxAddress.TryParse(envelope.MailFrom, out var mailFrom))
        {
            foreach (var address in envelope.Recipients)
            {
                trackedResults[address] = OutboundSubmitResult.PermanentFailure;
            }

            return BuildResults(envelope, trackedResults);
        }

        if (sendable.Count > 0)
        {
            try
            {
                await ConnectAndAuthenticateAsync(client, ct).ConfigureAwait(false);

                using var stream = new MemoryStream(rawMime);
                var message = await MimeMessage.LoadAsync(stream, ct).ConfigureAwait(false);

                await client.SendAsync(
                    FormatOptions.Default, message, mailFrom, sendable.Select(p => p.Parsed), ct)
                    .ConfigureAwait(false);
            }
            catch (SmtpCommandException ex) when (ex.ErrorCode == SmtpErrorCode.SenderNotAccepted)
            {
                // MAIL FROM itself was rejected: RCPT TO never ran, so no per-recipient outcome
                // exists yet - classify all sendable recipients uniformly from this one response.
                var result = SmtpErrorClassifier.Classify(ex);
                foreach (var (original, _) in sendable)
                {
                    trackedResults[original] = result;
                }
            }
            catch (SmtpCommandException ex)
            {
                // Either "no recipients were accepted" (every recipient already has a failure
                // recorded via OnRecipientNotAccepted, so there is nothing left to downgrade) or a
                // DATA-phase failure after some recipients were accepted - those still hold their
                // optimistic Success from OnRecipientAccepted and must be downgraded here.
                var result = SmtpErrorClassifier.Classify(ex);
                foreach (var (_, parsed) in sendable)
                {
                    if (trackedResults.TryGetValue(parsed.Address, out var current) && current == OutboundSubmitResult.Success)
                    {
                        trackedResults[parsed.Address] = result;
                    }
                }
            }
            catch (Exception)
            {
                // Any other exception (connect/TLS/auth/network) prevented recipients from being
                // reliably attempted at all; treat this as a transient infrastructure problem.
                foreach (var (original, _) in sendable)
                {
                    trackedResults[original] = OutboundSubmitResult.RetryableFailure;
                }
            }
            finally
            {
                if (client.IsConnected)
                {
                    try
                    {
                        await client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Best-effort disconnect; it does not change the already-decided outcome.
                    }
                }
            }
        }

        return BuildResults(envelope, trackedResults);
    }

    /// <summary>
    /// Maps the internal per-address <see cref="OutboundSubmitResult"/> tracking back to exactly one
    /// <see cref="SubmitOutcome"/> per envelope recipient (per the <see cref="IOutboundProvider"/>
    /// contract), checking both the caller's original recipient string and MailKit's normalized
    /// <see cref="MailboxAddress.Address"/> form, since different code paths key on different forms.
    /// </summary>
    private static IReadOnlyDictionary<string, SubmitOutcome> BuildResults(
        Envelope envelope, Dictionary<string, OutboundSubmitResult> trackedResults)
    {
        var results = new Dictionary<string, SubmitOutcome>();
        foreach (var original in envelope.Recipients)
        {
            if (trackedResults.TryGetValue(original, out var byOriginal))
            {
                results[original] = new SubmitOutcome(byOriginal);
            }
            else if (MailboxAddress.TryParse(original, out var parsed)
                && trackedResults.TryGetValue(parsed.Address, out var byParsed))
            {
                results[original] = new SubmitOutcome(byParsed);
            }
            else
            {
                // Defensive default: every recipient must have an outcome even if some
                // as-yet-unanticipated path left one unclassified.
                results[original] = new SubmitOutcome(OutboundSubmitResult.RetryableFailure);
            }
        }

        return results;
    }

    /// <summary>
    /// Performs an active, non-sending connectivity/health check: connect, negotiate TLS, and
    /// authenticate exactly as <see cref="Submit"/> does, then issue a NOOP and disconnect
    /// cleanly. The caller is responsible for bounding this with a timeout by passing a
    /// <paramref name="ct"/> already linked to one; no timeout is hardcoded here.
    /// </summary>
    public async Task<ProviderValidationResult> TestConnectionAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        using var client = new SmtpClient();

        try
        {
            await ConnectAndAuthenticateAsync(client, ct).ConfigureAwait(false);
            await client.NoOpAsync(ct).ConfigureAwait(false);
            return new ProviderValidationResult(true, null, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            return new ProviderValidationResult(false, DescribeFailure(ex), stopwatch.Elapsed);
        }
        finally
        {
            if (client.IsConnected)
            {
                try
                {
                    await client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort disconnect; the validation result is already decided.
                }
            }
        }
    }

    /// <summary>
    /// Shared connect+TLS+auth logic used by both <see cref="Submit"/> and
    /// <see cref="TestConnectionAsync"/>, so the validation check exercises exactly the same code
    /// path a real send would.
    /// </summary>
    private async Task ConnectAndAuthenticateAsync(SmtpClient client, CancellationToken ct)
    {
        if (_options.TrustAllCertificates)
        {
            // Insecure legacy escape hatch: accept any server certificate. Only used when the
            // caller has explicitly opted in via TrustAllCertificates.
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        }

        await client.ConnectAsync(_options.Host, _options.Port, MapTlsMode(_options.TlsMode), ct)
            .ConfigureAwait(false);

        switch (_options.AuthMode)
        {
            case AuthMode.None:
                break;
            case AuthMode.UsernamePassword:
                ArgumentNullException.ThrowIfNull(_options.Username);
                ArgumentNullException.ThrowIfNull(_options.Password);
                await client.AuthenticateAsync(_options.Username, _options.Password, ct).ConfigureAwait(false);
                break;
            case AuthMode.M365Oauth:
                ArgumentNullException.ThrowIfNull(_options.Username);
                ArgumentNullException.ThrowIfNull(_options.TokenProvider);
                var accessToken = await _options.TokenProvider.GetAccessTokenAsync(ct).ConfigureAwait(false);
                var oauth2 = new SaslMechanismOAuth2(_options.Username, accessToken);
                await client.AuthenticateAsync(oauth2, ct).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(_options.AuthMode), _options.AuthMode, "Unsupported AuthMode.");
        }
    }

    /// <summary>
    /// Classifies an exception from <see cref="TestConnectionAsync"/> into a short, fixed message
    /// that never echoes exception text - so a password/token can never leak into it, even if some
    /// unanticipated exception's own message happened to contain one.
    /// </summary>
    private static string DescribeFailure(Exception ex) => ex switch
    {
        OperationCanceledException => "Connection timed out.",
        AuthenticationException => "Authentication failed.",
        SmtpCommandException smtpEx => $"SMTP command failed: {smtpEx.StatusCode}.",
        SocketException socketEx => $"Connection failed: {socketEx.SocketErrorCode}.",
        _ => $"Connection failed: {ex.GetType().Name}.",
    };

    private static SecureSocketOptions MapTlsMode(SmtpTlsMode mode) => mode switch
    {
        SmtpTlsMode.StartTlsRequired => SecureSocketOptions.StartTls,
        SmtpTlsMode.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SmtpTlsMode.None => SecureSocketOptions.None,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported SmtpTlsMode."),
    };

    /// <summary>
    /// MailKit's high-level Send/SendAsync API aborts the whole transaction on the first
    /// rejected recipient. Overriding these two protected hooks (explicitly documented by
    /// MailKit as extension points for this purpose) lets us record each recipient's own
    /// accept/reject outcome instead, while still using the normal send codepath.
    /// </summary>
    private sealed class RecipientTrackingSmtpClient(Dictionary<string, OutboundSubmitResult> results) : SmtpClient
    {
        protected override void OnRecipientAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response)
        {
            // Optimistic: this may still be downgraded if the subsequent DATA phase fails.
            results[mailbox.Address] = OutboundSubmitResult.Success;
        }

        protected override void OnRecipientNotAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response)
        {
            // Deliberately does not call the base implementation (which throws), so a rejected
            // recipient is recorded rather than aborting the whole transaction.
            results[mailbox.Address] = SmtpErrorClassifier.Classify(response.StatusCode);
        }
    }
}
