using System.Net;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// Pure binding-policy decision for the inbound SMTP listener. By default the listener must be
/// loopback-only (never reachable from the LAN or a wildcard bind such as 0.0.0.0 / ::), so
/// <see cref="Validate"/> fails fast before the server is ever started. An operator can opt out
/// deliberately with <c>Smtp:AllowNonLoopbackBind</c>; <see cref="GetNonLoopbackEndpoints"/> lets
/// the caller discover which endpoints are non-loopback so it can emit the appropriate operator
/// warnings.
/// </summary>
public static class LoopbackEndpointValidator
{
    /// <summary>
    /// Returns the subset of <paramref name="endpoints"/> that are NOT loopback. A wildcard bind
    /// (0.0.0.0 / ::) is classified as non-loopback because it is reachable from the network, so it
    /// appears here too.
    /// </summary>
    public static IReadOnlyList<IPEndPoint> GetNonLoopbackEndpoints(IEnumerable<IPEndPoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints.Where(endpoint => !IPAddress.IsLoopback(endpoint.Address)).ToList();
    }

    /// <summary>
    /// Enforces the binding policy: when <paramref name="allowNonLoopback"/> is false and any
    /// endpoint is non-loopback (a specific LAN IP or a wildcard), throws. When true, both specific
    /// non-loopback IPs and wildcard binds are permitted. An empty endpoint list is always valid.
    /// </summary>
    public static void Validate(IEnumerable<IPEndPoint> endpoints, bool allowNonLoopback)
    {
        var nonLoopback = GetNonLoopbackEndpoints(endpoints);

        if (nonLoopback.Count > 0 && !allowNonLoopback)
        {
            var addresses = string.Join(", ", nonLoopback.Select(endpoint => endpoint.Address));
            throw new InvalidOperationException(
                $"Refusing to bind SMTP listener to non-loopback address(es) '{addresses}'. " +
                "Only 127.0.0.1 and ::1 are permitted by default; " +
                "set Smtp:AllowNonLoopbackBind to true to permit this deliberately.");
        }
    }
}
