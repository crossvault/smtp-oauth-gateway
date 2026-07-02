namespace SmtpGateway.Infrastructure;

/// <summary>
/// A token and the UTC instant it expires, as returned by an <see cref="ITokenAcquirer"/>.
/// </summary>
internal readonly record struct AcquiredToken(string AccessToken, DateTimeOffset ExpiresOnUtc);

/// <summary>
/// Thin seam over a real token acquisition mechanism (e.g. MSAL). Exists purely so that
/// <see cref="MsalTokenProvider"/>'s caching/refresh/single-flight logic can be unit tested with
/// a fake acquirer, without any real HTTP calls or MSAL setup.
/// </summary>
internal interface ITokenAcquirer
{
    /// <summary>Acquires a fresh access token from the underlying identity provider.</summary>
    Task<AcquiredToken> AcquireAsync(CancellationToken ct);
}
