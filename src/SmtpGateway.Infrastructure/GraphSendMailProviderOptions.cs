using SmtpGateway.Core;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// Configuration for <see cref="GraphSendMailProvider"/>: the Microsoft Graph sender mailbox and
/// bearer token source used for the single-call raw-MIME sendMail request.
/// </summary>
public sealed class GraphSendMailProviderOptions
{
    /// <summary>
    /// The single configured sender mailbox (e.g. "gateway@tenant.onmicrosoft.com"). Graph has no
    /// concept of deriving a mailbox dynamically from the envelope or MIME From header in this
    /// MVP - one provider instance always sends as this one mailbox.
    /// </summary>
    public required string Mailbox { get; init; }

    /// <summary>
    /// Supplies the bearer access token for Graph API calls (same <see cref="ITokenProvider"/>
    /// contract used by the M365 SMTP OAuth path, just acquired for the
    /// "https://graph.microsoft.com/.default" scope instead).
    /// </summary>
    public required ITokenProvider TokenProvider { get; init; }

    /// <summary>
    /// Base URL for the Graph API. Defaults to the real Graph v1.0 endpoint; overridable purely as
    /// a testability seam so tests can point this at a fake local HTTP endpoint instead of the
    /// real Graph API.
    /// </summary>
    public string GraphBaseUrl { get; init; } = "https://graph.microsoft.com/v1.0";
}
