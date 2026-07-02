using MailKit.Net.Smtp;
using SmtpGateway.Core;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// Classifies SMTP server responses/errors into an <see cref="OutboundSubmitResult"/>, per the
/// standard convention that 4xx codes are transient and 5xx codes are permanent.
/// </summary>
public static class SmtpErrorClassifier
{
    public static OutboundSubmitResult Classify(SmtpCommandException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Classify(exception.StatusCode);
    }

    public static OutboundSubmitResult Classify(SmtpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code >= 500 ? OutboundSubmitResult.PermanentFailure : OutboundSubmitResult.RetryableFailure;
    }
}
