using SmtpGateway.Core;
using Xunit;

namespace SmtpGateway.Core.Tests;

public class QueueItemStatusResolverTests
{
    private static RecipientDelivery Recipient(RecipientStatus status) =>
        new($"{status}@example.com", status);

    [Fact]
    public void AllPending_DerivesQueued()
    {
        var recipients = new[] { Recipient(RecipientStatus.Pending), Recipient(RecipientStatus.Pending) };

        var status = QueueItemStatusResolver.Derive(recipients);

        Assert.Equal(QueueItemStatus.Queued, status);
    }

    [Fact]
    public void AllSent_DerivesSent()
    {
        var recipients = new[] { Recipient(RecipientStatus.Sent), Recipient(RecipientStatus.Sent) };

        var status = QueueItemStatusResolver.Derive(recipients);

        Assert.Equal(QueueItemStatus.Sent, status);
    }

    [Fact]
    public void SentAndRetryable_DerivesPartiallySent()
    {
        var recipients = new[] { Recipient(RecipientStatus.Sent), Recipient(RecipientStatus.Retryable) };

        var status = QueueItemStatusResolver.Derive(recipients);

        Assert.Equal(QueueItemStatus.PartiallySent, status);
    }

    [Fact]
    public void SentAndPermanentlyFailed_DerivesPartiallySent()
    {
        var recipients = new[] { Recipient(RecipientStatus.Sent), Recipient(RecipientStatus.PermanentlyFailed) };

        var status = QueueItemStatusResolver.Derive(recipients);

        Assert.Equal(QueueItemStatus.PartiallySent, status);
    }

    [Fact]
    public void AllPermanentlyFailed_DerivesPoison()
    {
        // Poison: every recipient has permanently failed and none were ever sent or remain
        // retryable, so there is no further automated action possible - the item is dead
        // and requires operator attention, which is exactly what Poison represents.
        var recipients = new[] { Recipient(RecipientStatus.PermanentlyFailed), Recipient(RecipientStatus.PermanentlyFailed) };

        var status = QueueItemStatusResolver.Derive(recipients);

        Assert.Equal(QueueItemStatus.Poison, status);
    }

    [Fact]
    public void AllRetryable_DerivesRetryScheduled()
    {
        var recipients = new[] { Recipient(RecipientStatus.Retryable), Recipient(RecipientStatus.Retryable) };

        var status = QueueItemStatusResolver.Derive(recipients);

        Assert.Equal(QueueItemStatus.RetryScheduled, status);
    }

    [Fact]
    public void RetryableAndPermanentlyFailed_NoneSentOrPending_DerivesRetryScheduled()
    {
        var recipients = new[] { Recipient(RecipientStatus.Retryable), Recipient(RecipientStatus.PermanentlyFailed) };

        var status = QueueItemStatusResolver.Derive(recipients);

        Assert.Equal(QueueItemStatus.RetryScheduled, status);
    }

    [Fact]
    public void Derive_Throws_WhenNoRecipients()
    {
        Assert.Throws<ArgumentException>(() => QueueItemStatusResolver.Derive([]));
    }
}
