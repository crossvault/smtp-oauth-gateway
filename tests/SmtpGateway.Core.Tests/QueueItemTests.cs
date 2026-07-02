using SmtpGateway.Core;
using Xunit;

namespace SmtpGateway.Core.Tests;

public class QueueItemTests
{
    [Fact]
    public void Constructor_SetsAllProperties()
    {
        var id = Guid.NewGuid();
        var envelope = new Envelope("sender@example.com", ["a@example.com"]);
        var recipients = new List<RecipientDelivery> { new("a@example.com") };
        var createdAt = DateTimeOffset.UtcNow;

        var item = new QueueItem
        {
            Id = id,
            Envelope = envelope,
            Recipients = recipients,
            MimePath = @"C:\spool\a.eml",
            Hash = "deadbeef",
            SizeBytes = 1234,
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = createdAt,
            AttemptCount = 0,
            NextAttemptUtc = null,
            LeaseOwner = null,
            LeaseExpiryUtc = null,
            LastError = null,
            Status = QueueItemStatus.Queued,
        };

        Assert.Equal(id, item.Id);
        Assert.Same(envelope, item.Envelope);
        Assert.Same(recipients, item.Recipients);
        Assert.Equal(@"C:\spool\a.eml", item.MimePath);
        Assert.Equal("deadbeef", item.Hash);
        Assert.Equal(1234, item.SizeBytes);
        Assert.Equal(createdAt, item.CreatedAtUtc);
        Assert.Equal(createdAt, item.UpdatedAtUtc);
        Assert.Equal(0, item.AttemptCount);
        Assert.Null(item.NextAttemptUtc);
        Assert.Null(item.LeaseOwner);
        Assert.Null(item.LeaseExpiryUtc);
        Assert.Null(item.LastError);
        Assert.Equal(QueueItemStatus.Queued, item.Status);
    }

    [Fact]
    public void MutableFields_CanBeUpdatedAfterConstruction()
    {
        var item = new QueueItem
        {
            Id = Guid.NewGuid(),
            Envelope = new Envelope("sender@example.com", ["a@example.com"]),
            Recipients = [new RecipientDelivery("a@example.com")],
            MimePath = @"C:\spool\a.eml",
            Hash = "deadbeef",
            SizeBytes = 1234,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Status = QueueItemStatus.Queued,
        };

        item.Status = QueueItemStatus.Leased;
        item.LeaseOwner = "worker-1";
        item.AttemptCount = 1;

        Assert.Equal(QueueItemStatus.Leased, item.Status);
        Assert.Equal("worker-1", item.LeaseOwner);
        Assert.Equal(1, item.AttemptCount);
    }
}
