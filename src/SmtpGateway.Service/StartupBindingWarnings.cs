using System.Net;
using Microsoft.Extensions.Logging;

namespace SmtpGateway.Service;

/// <summary>
/// Emits the operator-facing startup warnings for a network-reachable inbound SMTP bind. Kept as a
/// pure, ILogger-driven decision (no real socket binding) so the warning matrix can be unit-tested
/// with a fake logger and a supplied endpoint classification. All warnings are <c>LogWarning</c> so
/// they are unmissable, and the AUTH password is never included in any of them.
/// </summary>
public static class StartupBindingWarnings
{
    public static void Log(
        ILogger logger, IReadOnlyList<IPEndPoint> nonLoopbackEndpoints, bool inboundAuthConfigured)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(nonLoopbackEndpoints);

        if (nonLoopbackEndpoints.Count == 0)
        {
            return;
        }

        var endpoints = string.Join(", ", nonLoopbackEndpoints.Select(endpoint => endpoint.ToString()));

        // (a) Always warn when the gateway is network-reachable.
        logger.LogWarning(
            "SECURITY: the SMTP gateway is bound to non-loopback endpoint(s) {Endpoints} and is therefore " +
            "reachable from the network, not just this host.",
            endpoints);

        if (!inboundAuthConfigured)
        {
            // (b) No inbound AUTH: anyone who can reach the port can relay mail through your provider.
            logger.LogWarning(
                "SECURITY: inbound SMTP AUTH is NOT configured while listening on non-loopback endpoint(s) " +
                "{Endpoints}. Any host that can reach the port can submit mail through this gateway to your " +
                "outbound provider (open-relay risk). Set Smtp:AuthUsername and Smtp:AuthPassword, or restrict " +
                "network access.",
                endpoints);
        }
        else
        {
            // (c) AUTH configured, but the inbound listener has no STARTTLS by design.
            logger.LogWarning(
                "SECURITY: inbound SMTP AUTH is enabled on non-loopback endpoint(s) {Endpoints}, but the inbound " +
                "listener has no STARTTLS by design - AUTH credentials cross the network in cleartext. Restrict " +
                "this endpoint to a trusted network.",
                endpoints);
        }
    }
}
