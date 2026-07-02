using System.Net;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// Pure parser/mapper from config-friendly bind endpoint strings (e.g. "127.0.0.1:2525" or
/// "[::1]:2525") to <see cref="IPEndPoint"/>. appsettings.json cannot naturally express an
/// <see cref="IPEndPoint"/> directly, so <see cref="SmtpInboundOptions.BindEndpoints"/> stores
/// strings and this type is the single place that turns them into the real type.
/// </summary>
public static class SmtpBindEndpointParser
{
    /// <summary>
    /// Parses a single "ip:port" (or bracketed "[ipv6]:port") string into an <see cref="IPEndPoint"/>.
    /// Only IP literals are accepted - no hostname resolution.
    /// </summary>
    public static IPEndPoint Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        // IPEndPoint.TryParse silently defaults to port 0 for an IP literal with no (unambiguous)
        // port suffix - e.g. a bare "127.0.0.1", or an unbracketed IPv6 literal like "::1:2525"
        // which it reads as the address itself rather than "::1" plus a port. Neither is a usable
        // SMTP bind endpoint, so those are rejected here as malformed rather than silently
        // defaulting.
        if (!IPEndPoint.TryParse(value, out var endpoint) || endpoint.Port == 0)
        {
            throw new FormatException(
                $"Invalid SMTP bind endpoint '{value}'. Expected an IP literal and an explicit port, " +
                "e.g. '127.0.0.1:2525' or '[::1]:2525'.");
        }

        return endpoint;
    }

    /// <summary>
    /// Parses every entry in <paramref name="values"/>. An empty input yields an empty result
    /// (no throw) - consistent with <see cref="LoopbackEndpointValidator.ValidateLoopbackOnly"/>,
    /// which also treats an empty endpoint list as valid.
    /// </summary>
    public static IReadOnlyList<IPEndPoint> ParseAll(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return values.Select(Parse).ToList();
    }
}
