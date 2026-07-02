namespace SmtpGateway.Core;

/// <summary>
/// Port for obtaining a bearer access token (e.g. for SMTP AUTH XOAUTH2 against Microsoft 365).
/// Implementations are responsible for any caching and refresh; callers should call this on
/// every use rather than caching the result themselves.
/// </summary>
public interface ITokenProvider
{
    /// <summary>Returns a valid bearer access token, acquiring or refreshing it if necessary.</summary>
    Task<string> GetAccessTokenAsync(CancellationToken ct);
}
