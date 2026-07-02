namespace SmtpGateway.Core;

/// <summary>
/// Decides the effective outbound MAIL FROM address to use when sending, given an optional
/// configured rewrite address. Pure string-in/string-out decision; does not touch MIME headers
/// (that requires MimeKit, which belongs in Infrastructure).
/// </summary>
public static class SenderRewritePolicy
{
    /// <summary>
    /// Returns <paramref name="configuredRewriteAddress"/> if it is configured (non-null and not
    /// empty/whitespace), otherwise returns <paramref name="originalMailFrom"/> unchanged.
    /// </summary>
    public static string Resolve(string originalMailFrom, string? configuredRewriteAddress) =>
        string.IsNullOrWhiteSpace(configuredRewriteAddress) ? originalMailFrom : configuredRewriteAddress;
}
