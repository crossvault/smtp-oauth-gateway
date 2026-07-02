using SmtpGateway.Infrastructure;
using Xunit;

namespace SmtpGateway.Infrastructure.Tests;

public sealed class GatewayOptionsValidatorTests
{
    private static GatewayOptions ValidOptions() => new()
    {
        Smtp = new SmtpInboundOptions { BindEndpoints = ["127.0.0.1:2525"] },
        SpoolDirectory = @"C:\spool",
        QueueDatabasePath = @"C:\queue.db",
        OutboundProvider = new OutboundProviderOptions
        {
            Provider = "GenericSmtp",
            GenericSmtp = new GenericSmtpSettings { Host = "relay.example.com", Port = 587 },
        },
    };

    [Fact]
    public void Validate_AcceptsFullyPopulatedOptions()
    {
        var exception = Record.Exception(() => GatewayOptionsValidator.Validate(ValidOptions()));

        Assert.Null(exception);
    }

    [Fact]
    public void Validate_ThrowsWhenSpoolDirectoryIsMissing()
    {
        var valid = ValidOptions();
        var options = new GatewayOptions
        {
            Smtp = valid.Smtp,
            SpoolDirectory = string.Empty,
            QueueDatabasePath = valid.QueueDatabasePath,
            OutboundProvider = valid.OutboundProvider,
        };

        Assert.Throws<InvalidOperationException>(() => GatewayOptionsValidator.Validate(options));
    }

    [Fact]
    public void Validate_ThrowsWhenBindEndpointsIsEmpty()
    {
        var valid = ValidOptions();
        var options = new GatewayOptions
        {
            Smtp = new SmtpInboundOptions { BindEndpoints = [] },
            SpoolDirectory = valid.SpoolDirectory,
            QueueDatabasePath = valid.QueueDatabasePath,
            OutboundProvider = valid.OutboundProvider,
        };

        Assert.Throws<InvalidOperationException>(() => GatewayOptionsValidator.Validate(options));
    }

    [Fact]
    public void Validate_ThrowsWhenBindEndpointIsMalformed()
    {
        var valid = ValidOptions();
        var options = new GatewayOptions
        {
            Smtp = new SmtpInboundOptions { BindEndpoints = ["not-an-endpoint"] },
            SpoolDirectory = valid.SpoolDirectory,
            QueueDatabasePath = valid.QueueDatabasePath,
            OutboundProvider = valid.OutboundProvider,
        };

        Assert.Throws<FormatException>(() => GatewayOptionsValidator.Validate(options));
    }

    [Fact]
    public void Validate_ThrowsWhenProviderNameIsMissing()
    {
        var valid = ValidOptions();
        var options = new GatewayOptions
        {
            Smtp = valid.Smtp,
            SpoolDirectory = valid.SpoolDirectory,
            QueueDatabasePath = valid.QueueDatabasePath,
            OutboundProvider = new OutboundProviderOptions { Provider = string.Empty },
        };

        Assert.Throws<InvalidOperationException>(() => GatewayOptionsValidator.Validate(options));
    }

    [Fact]
    public void EffectiveQueueTtl_CapsAtRetryPolicyMaxTtl()
    {
        var options = new GatewayOptions { QueueTtl = TimeSpan.FromDays(30) };

        Assert.Equal(SmtpGateway.Core.RetryPolicy.MaxTtl, options.EffectiveQueueTtl);
    }

    [Fact]
    public void Validate_MaxSpoolBytesNull_IsAccepted()
    {
        var valid = ValidOptions();
        var options = new GatewayOptions
        {
            Smtp = valid.Smtp,
            SpoolDirectory = valid.SpoolDirectory,
            QueueDatabasePath = valid.QueueDatabasePath,
            OutboundProvider = valid.OutboundProvider,
            MaxSpoolBytes = null,
        };

        var exception = Record.Exception(() => GatewayOptionsValidator.Validate(options));

        Assert.Null(exception);
    }

    [Fact]
    public void Validate_ThrowsWhenMaxSpoolBytesIsZeroOrNegative()
    {
        var valid = ValidOptions();
        var options = new GatewayOptions
        {
            Smtp = valid.Smtp,
            SpoolDirectory = valid.SpoolDirectory,
            QueueDatabasePath = valid.QueueDatabasePath,
            OutboundProvider = valid.OutboundProvider,
            MaxSpoolBytes = 0,
        };

        Assert.Throws<InvalidOperationException>(() => GatewayOptionsValidator.Validate(options));
    }

    [Fact]
    public void Validate_OutboundRateLimitPerMinuteNull_IsAccepted()
    {
        var valid = ValidOptions();
        var options = new GatewayOptions
        {
            Smtp = valid.Smtp,
            SpoolDirectory = valid.SpoolDirectory,
            QueueDatabasePath = valid.QueueDatabasePath,
            OutboundProvider = valid.OutboundProvider,
            OutboundRateLimitPerMinute = null,
        };

        var exception = Record.Exception(() => GatewayOptionsValidator.Validate(options));

        Assert.Null(exception);
    }

    [Fact]
    public void Validate_ThrowsWhenOutboundRateLimitPerMinuteIsZeroOrNegative()
    {
        var valid = ValidOptions();
        var options = new GatewayOptions
        {
            Smtp = valid.Smtp,
            SpoolDirectory = valid.SpoolDirectory,
            QueueDatabasePath = valid.QueueDatabasePath,
            OutboundProvider = valid.OutboundProvider,
            OutboundRateLimitPerMinute = 0,
        };

        Assert.Throws<InvalidOperationException>(() => GatewayOptionsValidator.Validate(options));
    }

    [Fact]
    public void Validate_AcceptsNullSenderRewriteAddress()
    {
        var valid = ValidOptions();
        var options = new GatewayOptions
        {
            Smtp = valid.Smtp,
            SpoolDirectory = valid.SpoolDirectory,
            QueueDatabasePath = valid.QueueDatabasePath,
            OutboundProvider = valid.OutboundProvider,
            SenderRewriteAddress = null,
        };

        Assert.Null(Record.Exception(() => GatewayOptionsValidator.Validate(options)));
    }

    [Fact]
    public void Validate_AcceptsValidSenderRewriteAddress()
    {
        var valid = ValidOptions();
        var options = new GatewayOptions
        {
            Smtp = valid.Smtp,
            SpoolDirectory = valid.SpoolDirectory,
            QueueDatabasePath = valid.QueueDatabasePath,
            OutboundProvider = valid.OutboundProvider,
            SenderRewriteAddress = "relay@example.com",
        };

        Assert.Null(Record.Exception(() => GatewayOptionsValidator.Validate(options)));
    }

    [Fact]
    public void Validate_ThrowsWhenSenderRewriteAddressIsMalformed()
    {
        var valid = ValidOptions();
        var options = new GatewayOptions
        {
            Smtp = valid.Smtp,
            SpoolDirectory = valid.SpoolDirectory,
            QueueDatabasePath = valid.QueueDatabasePath,
            OutboundProvider = valid.OutboundProvider,
            SenderRewriteAddress = "not a valid address",
        };

        Assert.Throws<FormatException>(() => GatewayOptionsValidator.Validate(options));
    }
}
