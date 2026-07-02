using MimeKit;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// Validates a bound <see cref="GatewayOptions"/> at startup. Invalid configuration must fail
/// fast (log and exit non-zero) - there is no degraded mode and no automatic repair.
/// </summary>
public static class GatewayOptionsValidator
{
    public static void Validate(GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        DataAnnotationsValidation.Validate(options, "Gateway");
        DataAnnotationsValidation.Validate(options.Smtp, "Smtp");
        DataAnnotationsValidation.Validate(options.OutboundProvider, "OutboundProvider");

        // [MinLength(1)] only guarantees non-empty entries exist; confirm they actually parse
        // as bind endpoints too, so a malformed value fails startup validation rather than
        // surfacing later as an obscure listener construction failure.
        SmtpBindEndpointParser.ParseAll(options.Smtp.BindEndpoints);

        // Inbound AUTH credentials are all-or-nothing: both empty disables AUTH, both set enables and
        // requires it. Exactly one is an operator mistake that would otherwise silently leave AUTH off.
        var hasUsername = !string.IsNullOrWhiteSpace(options.Smtp.AuthUsername);
        var hasPassword = !string.IsNullOrWhiteSpace(options.Smtp.AuthPassword);
        if (hasUsername != hasPassword)
        {
            throw new InvalidOperationException(
                "Inbound SMTP AUTH is misconfigured: set both Smtp:AuthUsername and Smtp:AuthPassword to " +
                "enable authentication, or leave both empty to disable it.");
        }

        // A configured SenderRewriteAddress is parsed with MailboxAddress.Parse at delivery time
        // (OutboundDeliveryWorker.RewriteFromHeaderAsync); if it were malformed the failure would
        // otherwise surface only later as a per-item delivery error. Validate it here so a bad
        // rewrite address fails fast at startup, consistent with the fail-fast ValidateOnStart
        // design. Null/empty means "no rewrite" and is left untouched.
        if (!string.IsNullOrWhiteSpace(options.SenderRewriteAddress)
            && !MailboxAddress.TryParse(options.SenderRewriteAddress, out _))
        {
            throw new FormatException(
                $"Gateway:SenderRewriteAddress '{options.SenderRewriteAddress}' is not a valid email address.");
        }
    }
}
