using System.Security.Cryptography;
using System.Text;
using SmtpServer;
using SmtpServer.Authentication;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// <see cref="IUserAuthenticator"/> for inbound SMTP AUTH (PLAIN/LOGIN) that accepts exactly one
/// configured username/password pair. Both are compared with
/// <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/> so
/// a wrong guess cannot be distinguished from a right one by timing. Wired into
/// <see cref="SmtpGatewayListener"/> only when both credentials are configured.
/// </summary>
public sealed class FixedCredentialUserAuthenticator : IUserAuthenticator
{
    private readonly byte[] _expectedUsername;
    private readonly byte[] _expectedPassword;

    public FixedCredentialUserAuthenticator(string username, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(password);

        _expectedUsername = Encoding.UTF8.GetBytes(username);
        _expectedPassword = Encoding.UTF8.GetBytes(password);
    }

    public Task<bool> AuthenticateAsync(
        ISessionContext context, string user, string password, CancellationToken cancellationToken)
    {
        // Evaluate both comparisons unconditionally (no short-circuit) so overall timing does not
        // reveal whether it was the username or the password that failed.
        var userMatches = FixedTimeEquals(user, _expectedUsername);
        var passwordMatches = FixedTimeEquals(password, _expectedPassword);

        return Task.FromResult(userMatches && passwordMatches);
    }

    private static bool FixedTimeEquals(string? candidate, byte[] expected)
    {
        var candidateBytes = Encoding.UTF8.GetBytes(candidate ?? string.Empty);
        return CryptographicOperations.FixedTimeEquals(candidateBytes, expected);
    }
}
