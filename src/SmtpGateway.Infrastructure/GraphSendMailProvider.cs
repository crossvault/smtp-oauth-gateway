using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using SmtpGateway.Core;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// Outbound provider that sends mail via Microsoft Graph's raw-MIME sendMail endpoint in a single
/// call: 'POST /users/{mailbox}/sendMail' with the base64-encoded raw MIME as the request body
/// (Content-Type: text/plain). A 202 Accepted is treated as Sent - this is Graph's own acceptance
/// signal, NOT a real final-delivery confirmation; that limitation is inherent to the API and not
/// something this provider can fix. This single-call path needs only the low-privilege 'Mail.Send'
/// application permission (the older draft-create-then-send round trip required 'Mail.ReadWrite').
/// <para>
/// Graph has no concept of "reject one recipient, accept the others" - the whole message is a
/// single unit as far as the API is concerned, so a single Submit call's outcome is applied
/// uniformly to every recipient in the envelope. This is a known Graph limitation, not an
/// attempt to fake per-recipient granularity that the underlying API simply does not provide.
/// </para>
/// </summary>
public sealed class GraphSendMailProvider : IOutboundProvider
{
    private readonly GraphSendMailProviderOptions _options;
    private readonly HttpClient _httpClient;

    public GraphSendMailProvider(GraphSendMailProviderOptions options)
        : this(options, new HttpClient())
    {
    }

    /// <summary>
    /// Test-only seam: lets tests supply an <see cref="HttpClient"/> wired to a fake
    /// <see cref="HttpMessageHandler"/> instead of hitting the real network.
    /// </summary>
    internal GraphSendMailProvider(GraphSendMailProviderOptions options, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        _options = options;
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyDictionary<string, SubmitOutcome>> Submit(
        Envelope envelope, byte[] rawMime, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(rawMime);

        var outcome = await SubmitCoreAsync(envelope, rawMime, ct).ConfigureAwait(false);

        var results = new Dictionary<string, SubmitOutcome>(StringComparer.OrdinalIgnoreCase);
        foreach (var recipient in envelope.Recipients)
        {
            results[recipient] = outcome;
        }

        return results;
    }

    /// <summary>
    /// Active, non-sending mailbox-reachability check: acquires a bearer token exactly as
    /// <see cref="Submit"/> does, then issues a plain 'GET /users/{mailbox}' - Graph's lightweight
    /// "does this mailbox exist and can I read it" call - without ever creating or sending a
    /// message. The caller is responsible for bounding this with a timeout by passing a
    /// <paramref name="ct"/> already linked to one; no timeout is hardcoded here.
    /// </summary>
    public async Task<ProviderValidationResult> TestMailboxAccessAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var token = await _options.TokenProvider.GetAccessTokenAsync(ct).ConfigureAwait(false);
            var mailboxSegment = Uri.EscapeDataString(_options.Mailbox);

            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{_options.GraphBaseUrl}/users/{mailboxSegment}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return new ProviderValidationResult(true, null, stopwatch.Elapsed);
            }

            return new ProviderValidationResult(
                false, $"Graph mailbox check failed: {(int)response.StatusCode} {response.StatusCode}.", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            return new ProviderValidationResult(false, DescribeFailure(ex), stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// Classifies an exception from <see cref="TestMailboxAccessAsync"/> into a short, fixed
    /// message that never echoes exception text - so a bearer token can never leak into it, even
    /// if some unanticipated exception's own message happened to contain one.
    /// </summary>
    private static string DescribeFailure(Exception ex) => ex switch
    {
        OperationCanceledException => "Request timed out.",
        HttpRequestException httpEx => $"HTTP request failed: {httpEx.StatusCode?.ToString() ?? "network error"}.",
        _ => $"Request failed: {ex.GetType().Name}.",
    };

    private async Task<SubmitOutcome> SubmitCoreAsync(Envelope envelope, byte[] rawMime, CancellationToken ct)
    {
        try
        {
            var token = await _options.TokenProvider.GetAccessTokenAsync(ct).ConfigureAwait(false);
            var mailboxSegment = Uri.EscapeDataString(_options.Mailbox);

            // Graph's sendMail endpoint accepts a full raw-MIME message when the request body is the
            // base64-encoded MIME content with a 'Content-Type: text/plain' header. This single call
            // sends the message directly (202 Accepted) and needs only the 'Mail.Send' application
            // permission - no draft is created, so no 'Mail.ReadWrite' is required.
            var base64Mime = Convert.ToBase64String(rawMime);
            using var sendContent = new StringContent(base64Mime, System.Text.Encoding.ASCII);
            sendContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

            using var sendRequest = new HttpRequestMessage(
                HttpMethod.Post, $"{_options.GraphBaseUrl}/users/{mailboxSegment}/sendMail")
            {
                Content = sendContent,
            };
            sendRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var sendResponse = await _httpClient.SendAsync(sendRequest, ct).ConfigureAwait(false);
            if (!sendResponse.IsSuccessStatusCode)
            {
                return ClassifyFailure(sendResponse);
            }

            return new SubmitOutcome(OutboundSubmitResult.Success);
        }
        catch (Exception) when (ct.IsCancellationRequested is false)
        {
            // Network exceptions, timeouts, an unexpected/malformed response body, or any other
            // transport-level failure: treat as a transient infrastructure problem (established
            // convention shared with GenericSmtpProvider). Deliberately never includes the
            // exception's own message/details here - only a fixed outcome is returned - so no
            // header or body content (which could carry the bearer token) ever surfaces.
            return new SubmitOutcome(OutboundSubmitResult.RetryableFailure);
        }
    }

    private static SubmitOutcome ClassifyFailure(HttpResponseMessage response)
    {
        if (response.StatusCode == (HttpStatusCode)429)
        {
            return new SubmitOutcome(OutboundSubmitResult.RetryableFailure, ParseRetryAfter(response));
        }

        if ((int)response.StatusCode >= 500)
        {
            return new SubmitOutcome(OutboundSubmitResult.RetryableFailure);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // A transient token issue (expired bearer token between acquisition and the Graph call,
            // clock skew, brief AAD hiccup) surfaces as 401 - re-acquiring a fresh token on the next
            // attempt can succeed, so treat this as retryable rather than permanent.
            return new SubmitOutcome(OutboundSubmitResult.RetryableFailure);
        }

        // Other 4xx (400 bad request, 403 permission problems, 404 mailbox not found) are not
        // transient - retrying without an operator fixing the underlying config/permission problem
        // would not help.
        return new SubmitOutcome(OutboundSubmitResult.PermanentFailure);
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        return null;
    }
}
