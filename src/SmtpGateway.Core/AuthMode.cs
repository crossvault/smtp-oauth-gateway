namespace SmtpGateway.Core;

/// <summary>
/// The fixed set of authentication modes an outbound provider can use. There are exactly three
/// built-ins by design; this is a plain enum, not a plugin/registry, per product decision.
/// </summary>
public enum AuthMode
{
    /// <summary>No authentication.</summary>
    None,

    /// <summary>Plain username/password authentication.</summary>
    UsernamePassword,

    /// <summary>Microsoft 365 OAuth (MSAL), see Phase 3.</summary>
    M365Oauth,
}
