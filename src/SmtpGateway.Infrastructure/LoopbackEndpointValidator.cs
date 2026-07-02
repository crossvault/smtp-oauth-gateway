using System.Net;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// Fails fast if any configured SMTP bind endpoint is not loopback-only. The local SMTP
/// listener must never be reachable from the LAN or a wildcard bind (0.0.0.0 / ::), so this
/// is enforced before the server is ever started.
/// </summary>
public static class LoopbackEndpointValidator
{
    public static void ValidateLoopbackOnly(IEnumerable<IPEndPoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        foreach (var endpoint in endpoints)
        {
            if (!IPAddress.IsLoopback(endpoint.Address))
            {
                throw new InvalidOperationException(
                    $"Refusing to bind SMTP listener to non-loopback address '{endpoint.Address}'. " +
                    "Only 127.0.0.1 and ::1 are permitted.");
            }
        }
    }
}
