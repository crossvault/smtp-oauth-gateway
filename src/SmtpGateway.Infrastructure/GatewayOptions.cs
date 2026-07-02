using System.ComponentModel.DataAnnotations;
using SmtpGateway.Core;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// Top-level, bind-once-at-startup configuration for the gateway service. No hot reload is
/// supported by design - a process restart is required after any change. See
/// <see cref="GatewayOptionsValidator"/> for the startup validation pass and
/// <see cref="OutboundProviderFactory"/> for turning <see cref="OutboundProvider"/> into a real
/// <see cref="IOutboundProvider"/>.
/// </summary>
public sealed class GatewayOptions
{
    [Required]
    public SmtpInboundOptions Smtp { get; init; } = new();

    /// <summary>Root directory of the durable raw-MIME file spool (<see cref="FileSpool"/>).</summary>
    [Required(AllowEmptyStrings = false)]
    public string SpoolDirectory { get; init; } = string.Empty;

    /// <summary>Path of the SQLite queue database file (<see cref="SqliteQueueRepository"/>).</summary>
    [Required(AllowEmptyStrings = false)]
    public string QueueDatabasePath { get; init; } = string.Empty;

    /// <summary>Configured queue item time-to-live; see <see cref="EffectiveQueueTtl"/> for the capped value actually used.</summary>
    public TimeSpan QueueTtl { get; init; } = RetryPolicy.DefaultTtl;

    /// <summary><see cref="QueueTtl"/> after applying <see cref="RetryPolicy.ValidateTtl"/>'s cap.</summary>
    public TimeSpan EffectiveQueueTtl => RetryPolicy.ValidateTtl(QueueTtl);

    /// <summary>Lease duration the delivery worker holds a queue item for while attempting delivery.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Optional address to rewrite the MIME "From:" header to before outbound submission; null/empty leaves it untouched.</summary>
    public string? SenderRewriteAddress { get; init; }

    /// <summary>How long the outbound delivery loop waits after finding an empty queue before checking again.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan DeliveryPollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the outbound delivery loop runs the TTL-expiry sweep (see <see cref="TtlSweepPolicy"/>).</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan TtlSweepInterval { get; init; } = TimeSpan.FromMinutes(15);

    [Required]
    public OutboundProviderOptions OutboundProvider { get; init; } = new();

    /// <summary>
    /// Optional maximum total spool footprint in bytes - the sum of every queue item's
    /// <see cref="Core.QueueItem.SizeBytes"/> regardless of status (see
    /// <see cref="SqliteQueueRepository.GetTotalSpoolBytesAsync"/>), since nothing ever deletes a
    /// spool file. Null (the default) means unlimited - only operators who explicitly configure
    /// this get inbound backpressure; see <see cref="SpoolingMessageStore"/>.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long? MaxSpoolBytes { get; init; }

    /// <summary>
    /// Optional cap on how many outbound provider submissions the delivery loop makes per rolling
    /// minute (see <see cref="SlidingWindowRateLimiter"/>). Null (the default) means unlimited -
    /// only operators who explicitly configure this get throttling, since most providers have no
    /// rate limit worth guarding against.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int? OutboundRateLimitPerMinute { get; init; }
}
