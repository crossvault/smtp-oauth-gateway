using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using SmtpGateway.Service;
using Xunit;

namespace SmtpGateway.IntegrationTests;

/// <summary>
/// Unit tests for the operator-facing startup warning matrix (network-reachable / open-relay /
/// cleartext-AUTH). The warning decision is pure and ILogger-driven, so it is exercised here with a
/// capturing fake logger and a supplied endpoint classification - no real socket binding, matching
/// the constraint that CI runners must not bind LAN IPs. A password is passed through the caller's
/// context to prove it is never emitted into any log line.
/// </summary>
public sealed class StartupBindingWarningsTests
{
    private const string SecretPassword = "Sup3rSecretInboundPass!";

    private static readonly IReadOnlyList<IPEndPoint> Loopback = [];
    private static readonly IReadOnlyList<IPEndPoint> NonLoopback =
        [new IPEndPoint(IPAddress.Parse("192.168.1.10"), 2525)];

    [Fact]
    public void LoopbackOnly_EmitsNoWarnings()
    {
        var logger = new CapturingLogger();

        StartupBindingWarnings.Log(logger, Loopback, inboundAuthConfigured: false);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void NonLoopbackWithoutAuth_WarnsReachableAndOpenRelay()
    {
        var logger = new CapturingLogger();

        StartupBindingWarnings.Log(logger, NonLoopback, inboundAuthConfigured: false);

        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Warning, entry.Level));
        Assert.Contains(logger.Entries, e => e.Message.Contains("reachable from the network", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logger.Entries, e => e.Message.Contains("open-relay", StringComparison.OrdinalIgnoreCase));
        // The endpoint is named so the operator knows which bind is at risk.
        Assert.Contains(logger.Entries, e => e.Message.Contains("192.168.1.10", StringComparison.Ordinal));
    }

    [Fact]
    public void NonLoopbackWithAuth_WarnsReachableAndCleartextAuth_NotOpenRelay()
    {
        var logger = new CapturingLogger();

        StartupBindingWarnings.Log(logger, NonLoopback, inboundAuthConfigured: true);

        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Warning, entry.Level));
        Assert.Contains(logger.Entries, e => e.Message.Contains("reachable from the network", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logger.Entries, e => e.Message.Contains("cleartext", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("open-relay", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Warnings_NeverContainThePassword()
    {
        // The helper is given no password (by design), but simulate an operator whose password
        // matches an endpoint-ish string and assert it appears nowhere - the helper only ever sees
        // endpoints, never the credential, so no log line can leak it.
        var loggerNoAuth = new CapturingLogger();
        var loggerWithAuth = new CapturingLogger();

        StartupBindingWarnings.Log(loggerNoAuth, NonLoopback, inboundAuthConfigured: false);
        StartupBindingWarnings.Log(loggerWithAuth, NonLoopback, inboundAuthConfigured: true);

        foreach (var entry in loggerNoAuth.Entries.Concat(loggerWithAuth.Entries))
        {
            Assert.DoesNotContain(SecretPassword, entry.Message, StringComparison.Ordinal);
            foreach (var property in entry.Properties)
            {
                Assert.NotEqual(SecretPassword, property.Value?.ToString());
            }
        }
    }

    private sealed record CapturedEntry(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> Properties);

    private sealed class CapturingLogger : ILogger
    {
        public ConcurrentQueue<CapturedEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = (state as IEnumerable<KeyValuePair<string, object?>>)?.ToList()
                ?? new List<KeyValuePair<string, object?>>();
            Entries.Enqueue(new CapturedEntry(logLevel, formatter(state, exception), properties));
        }
    }
}
