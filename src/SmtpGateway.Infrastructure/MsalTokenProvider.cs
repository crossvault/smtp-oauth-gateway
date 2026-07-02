using SmtpGateway.Core;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// <see cref="ITokenProvider"/> that caches the acquired access token in memory and refreshes it
/// shortly before it expires. Concurrent callers that observe no valid cached token share a
/// single in-flight acquisition (single-flight) rather than each triggering their own call to the
/// underlying <see cref="ITokenAcquirer"/>. The token is never persisted to disk; it only lives
/// for the lifetime of this instance.
/// </summary>
public sealed class MsalTokenProvider : ITokenProvider
{
    // Refresh this long before expiry so a token is never handed out that could expire mid-use.
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly ITokenAcquirer _acquirer;
    private readonly Lock _gate = new();
    private AcquiredToken? _cached;
    private Task<AcquiredToken>? _inFlight;

    internal MsalTokenProvider(ITokenAcquirer acquirer)
    {
        ArgumentNullException.ThrowIfNull(acquirer);
        _acquirer = acquirer;
    }

    /// <summary>
    /// Convenience constructor that wires up a real MSAL-backed acquirer for the given Microsoft
    /// Entra client-credentials configuration.
    /// </summary>
    public MsalTokenProvider(string tenantId, string clientId, string clientSecret, string scope)
        : this(new MsalTokenAcquirer(tenantId, clientId, clientSecret, scope))
    {
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        try
        {
            Task<AcquiredToken> acquisition;

            lock (_gate)
            {
                if (_cached is { } cached && !IsNearExpiry(cached))
                {
                    return cached.AccessToken;
                }

                // The shared in-flight acquisition deliberately runs with CancellationToken.None,
                // not any individual caller's token: it is joined by every concurrent caller that
                // observes no valid cached token, and one joiner cancelling must not fault the
                // acquisition (and thus every other joiner) out from under callers whose own
                // tokens are still live. Each caller instead applies its own token only to its
                // local wait below via WaitAsync, so cancelling caller A's token only cancels A's
                // wait, leaving the shared task (and caller B) unaffected. Deliberately does not
                // take _gate again inside a continuation of this call: the underlying acquirer
                // may complete synchronously (as fakes in tests do), and re-entering _gate from
                // within this same synchronous call chain, while _gate is still held above, would
                // corrupt the in-flight/cache bookkeeping below. Instead every caller applies the
                // result to the cache itself once it observes completion, safely outside of this
                // lock scope.
                _inFlight ??= _acquirer.AcquireAsync(CancellationToken.None);
                acquisition = _inFlight;
            }

            var acquired = await acquisition.WaitAsync(ct).ConfigureAwait(false);

            lock (_gate)
            {
                _cached = acquired;
                _inFlight = null;
            }

            return acquired.AccessToken;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation is caller-initiated (via this caller's own WaitAsync), not an
            // acquisition failure; let it propagate as-is instead of being reported as "token
            // acquisition failed." The shared task itself was given CancellationToken.None, so it
            // keeps running for any other joiner - do not clear _inFlight here.
            throw;
        }
        catch
        {
            lock (_gate)
            {
                _inFlight = null;
            }

            throw new InvalidOperationException("Token acquisition failed.");
        }
    }

    private static bool IsNearExpiry(AcquiredToken token) =>
        DateTimeOffset.UtcNow >= token.ExpiresOnUtc - RefreshMargin;
}
