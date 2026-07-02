namespace SmtpGateway.Core;

/// <summary>
/// The per-recipient delivery state for a single mail.
/// </summary>
public enum RecipientStatus
{
    /// <summary>No delivery attempt has produced a final or retryable outcome yet.</summary>
    Pending,

    /// <summary>The provider accepted the message for this recipient.</summary>
    Sent,

    /// <summary>The last attempt failed with a transient error; another attempt is scheduled.</summary>
    Retryable,

    /// <summary>The last attempt failed with a non-transient error; no further attempts will be made.</summary>
    PermanentlyFailed,
}
