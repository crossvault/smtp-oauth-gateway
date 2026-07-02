using SmtpGateway.Infrastructure;
using Xunit;

namespace SmtpGateway.Infrastructure.Tests;

public sealed class FixedCredentialUserAuthenticatorTests
{
    private static async Task<bool> AuthenticateAsync(string user, string password)
    {
        var authenticator = new FixedCredentialUserAuthenticator("relay-user", "s3cr3t-p@ss");
        return await authenticator.AuthenticateAsync(
            context: null!, user, password, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Authenticate_CorrectCredentials_ReturnsTrue()
    {
        Assert.True(await AuthenticateAsync("relay-user", "s3cr3t-p@ss"));
    }

    [Theory]
    [InlineData("relay-user", "wrong")]
    [InlineData("wrong", "s3cr3t-p@ss")]
    [InlineData("RELAY-USER", "s3cr3t-p@ss")] // ordinal, case-sensitive
    [InlineData("relay-user", "S3CR3T-P@SS")]
    [InlineData("", "")]
    [InlineData("relay-user", "")]
    public async Task Authenticate_WrongCredentials_ReturnsFalse(string user, string password)
    {
        Assert.False(await AuthenticateAsync(user, password));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", null)]
    public void Constructor_RejectsNullOrEmptyCredentials(string? username, string? password)
    {
        Assert.ThrowsAny<ArgumentException>(() => new FixedCredentialUserAuthenticator(username!, password!));
    }
}
