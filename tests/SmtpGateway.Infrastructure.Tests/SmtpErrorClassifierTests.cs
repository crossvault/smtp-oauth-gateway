using MailKit.Net.Smtp;
using SmtpGateway.Core;
using Xunit;

namespace SmtpGateway.Infrastructure.Tests;

public sealed class SmtpErrorClassifierTests
{
    [Theory]
    [InlineData(SmtpStatusCode.ServiceNotAvailable)] // 421
    [InlineData(SmtpStatusCode.MailboxBusy)] // 450
    [InlineData(SmtpStatusCode.ErrorInProcessing)] // 451
    [InlineData(SmtpStatusCode.InsufficientStorage)] // 452
    public void Classify_4xxStatusCode_IsRetryableFailure(SmtpStatusCode statusCode)
    {
        var result = SmtpErrorClassifier.Classify(statusCode);

        Assert.Equal(OutboundSubmitResult.RetryableFailure, result);
    }

    [Theory]
    [InlineData(SmtpStatusCode.CommandUnrecognized)] // 500
    [InlineData(SmtpStatusCode.MailboxUnavailable)] // 550
    [InlineData(SmtpStatusCode.TransactionFailed)] // 554
    public void Classify_5xxStatusCode_IsPermanentFailure(SmtpStatusCode statusCode)
    {
        var result = SmtpErrorClassifier.Classify(statusCode);

        Assert.Equal(OutboundSubmitResult.PermanentFailure, result);
    }

    [Fact]
    public void Classify_SmtpCommandExceptionWith4xxStatusCode_IsRetryableFailure()
    {
        var exception = new SmtpCommandException(
            SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxBusy, "Mailbox busy, try again later.");

        var result = SmtpErrorClassifier.Classify(exception);

        Assert.Equal(OutboundSubmitResult.RetryableFailure, result);
    }

    [Fact]
    public void Classify_SmtpCommandExceptionWith5xxStatusCode_IsPermanentFailure()
    {
        var exception = new SmtpCommandException(
            SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, "Mailbox unavailable.");

        var result = SmtpErrorClassifier.Classify(exception);

        Assert.Equal(OutboundSubmitResult.PermanentFailure, result);
    }

    [Fact]
    public void Classify_NullException_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => SmtpErrorClassifier.Classify(exception: null!));
    }
}
