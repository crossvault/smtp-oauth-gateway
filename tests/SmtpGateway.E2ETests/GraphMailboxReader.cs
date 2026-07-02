using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SmtpGateway.Core;

namespace SmtpGateway.E2ETests;

/// <summary>
/// TEST-ONLY Microsoft Graph mailbox reader used by the content-fidelity live tests to verify what
/// actually landed in a recipient's mailbox (closing the gap the spool checks cannot: they only
/// prove what the gateway persisted, not what M365 finally delivered). It authenticates exactly the
/// way <see cref="SmtpGateway.Infrastructure.GraphSendMailProvider"/> does - a plain
/// <see cref="HttpClient"/> plus a Graph-scope <see cref="ITokenProvider"/> bearer token - but lives
/// only in the test project and is never part of the shipped product.
/// <para>
/// It finds a message by its unique GUID subject via
/// <c>GET /users/{recipient}/messages?$filter=subject eq '{subject}'</c> (polled until the message
/// replicates into the mailbox or the timeout elapses), then fetches the FULL received MIME via
/// <c>GET /users/{recipient}/messages/{id}/$value</c> so every assertion (attachment hashes, bodies,
/// headers) can reuse the same <see cref="SpooledMime"/> helpers symmetrically with the spool checks.
/// </para>
/// <para>
/// Requires the <c>Mail.Read</c> Graph application permission; callers gate on it via
/// <see cref="GraphAppRoles"/> and skip when it is absent. The reads are read-only: Mail.Read cannot
/// delete the received test mails (that needs Mail.ReadWrite), so the tests deliberately leave them
/// in the sandbox mailboxes - the sandbox tolerates the modest accumulation. The bearer token is auth
/// material and is never logged, echoed, or placed into any failure message; failures surface only a
/// fixed status code, never a response body or the mailbox address.
/// </para>
/// </summary>
internal sealed class GraphMailboxReader : IDisposable
{
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";

    /// <summary>Default mailbox-arrival poll interval - Exchange internal delivery/indexing lags seconds.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient = new();
    private readonly ITokenProvider _tokenProvider;

    public GraphMailboxReader(ITokenProvider tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        _tokenProvider = tokenProvider;
    }

    /// <summary>
    /// Polls the mailbox for a message whose subject exactly matches <paramref name="subject"/> and
    /// returns its raw received MIME bytes once found, or throws a secret-free
    /// <see cref="TimeoutException"/> if none arrives within <paramref name="timeout"/>. The subject
    /// is the caller's unique GUID-bearing test subject, so an exact-match filter is unambiguous.
    /// </summary>
    public async Task<byte[]> WaitForRawMimeAsync(
        string mailbox, string subject, TimeSpan timeout, CancellationToken ct)
    {
        var id = await PollForMessageIdAsync(mailbox, subject, timeout, ct).ConfigureAwait(false)
            ?? throw new TimeoutException(
                "A message with the expected unique test subject did not arrive in the recipient "
                + $"mailbox within {timeout.TotalSeconds:0}s.");

        return await FetchRawMimeAsync(mailbox, id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns whether ANY message with the given subject has arrived in the mailbox within the
    /// timeout. Used by the BCC test to prove the blind-copy landed in the BCC recipient's own
    /// mailbox without needing to download its body.
    /// </summary>
    public async Task<bool> MessageArrivedAsync(
        string mailbox, string subject, TimeSpan timeout, CancellationToken ct)
    {
        var id = await PollForMessageIdAsync(mailbox, subject, timeout, ct).ConfigureAwait(false);
        return id is not null;
    }

    private async Task<string?> PollForMessageIdAsync(
        string mailbox, string subject, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var id = await TryFindMessageIdAsync(mailbox, subject, ct).ConfigureAwait(false);
            if (id is not null)
            {
                return id;
            }

            if (deadline.Elapsed + PollInterval >= timeout)
            {
                return null;
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    private async Task<string?> TryFindMessageIdAsync(string mailbox, string subject, CancellationToken ct)
    {
        // OData string literals escape an embedded single quote by doubling it. The test subjects
        // never contain one, but escape defensively rather than emit an invalid filter.
        var filter = $"subject eq '{subject.Replace("'", "''", StringComparison.Ordinal)}'";
        var url = $"{GraphBaseUrl}/users/{Uri.EscapeDataString(mailbox)}/messages"
            + $"?$select=id&$top=5&$filter={Uri.EscapeDataString(filter)}";

        using var response = await SendAuthorizedAsync(HttpMethod.Get, url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // A transient throttle (429) or 5xx hiccup on a single poll must not abort the whole
            // test: the message would still be found on a later iteration. Treat it exactly like a
            // 'not yet arrived' result so the caller keeps polling (the loop already waits the poll
            // interval before retrying). Only genuinely fatal, non-self-healing statuses - e.g. 401
            // (bad token) or 403 (missing Mail.Read) - fail fast, since polling could never fix them.
            if (IsTransient(response.StatusCode))
            {
                return null;
            }

            throw new HttpRequestException(
                $"Graph message search failed: {(int)response.StatusCode} {response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var element in value.EnumerateArray())
        {
            if (element.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } messageId)
            {
                return messageId;
            }
        }

        return null;
    }

    private async Task<byte[]> FetchRawMimeAsync(string mailbox, string messageId, CancellationToken ct)
    {
        // '$value' on a message returns its full received MIME (message/rfc822) exactly as Exchange
        // re-emitted it - the symmetric counterpart to the gateway's spooled MIME.
        var url = $"{GraphBaseUrl}/users/{Uri.EscapeDataString(mailbox)}/messages/"
            + $"{Uri.EscapeDataString(messageId)}/$value";

        using var response = await SendAuthorizedAsync(HttpMethod.Get, url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Graph raw-MIME fetch failed: {(int)response.StatusCode} {response.StatusCode}.");
        }

        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a non-success status is a transient hiccup a mailbox poll should ride out (keep
    /// polling) rather than fail on: Graph request throttling (429) and the transient 5xx service
    /// errors. Everything else - notably 401/403/404 - is treated as fatal, because repeated polling
    /// could never turn it into a success.
    /// </summary>
    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests        // 429
            or HttpStatusCode.InternalServerError       // 500
            or HttpStatusCode.BadGateway                // 502
            or HttpStatusCode.ServiceUnavailable        // 503
            or HttpStatusCode.GatewayTimeout;           // 504

    private async Task<HttpResponseMessage> SendAuthorizedAsync(HttpMethod method, string url, CancellationToken ct)
    {
        var token = await _tokenProvider.GetAccessTokenAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
    }

    public void Dispose() => _httpClient.Dispose();
}
