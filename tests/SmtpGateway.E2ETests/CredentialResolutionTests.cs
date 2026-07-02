using Xunit;

namespace SmtpGateway.E2ETests;

/// <summary>
/// Unit tests for the .env-over-environment precedence rule in <see cref="E2ECredentials.Resolve"/>.
/// These are NOT live tests: they inject a fake <c>.env</c> dictionary and a fake environment lookup
/// delegate, so they run everywhere without the developer's real <c>.env</c> or the live tenant and
/// never self-skip. They are parallel-safe because they mutate no global state (no real environment
/// variables, no real files).
/// <para>
/// Tradeoff (see also <see cref="E2ECredentials.Resolve"/>): they live in the E2ETests project, which
/// <c>.github/workflows/ci.yml</c> excludes, so ci.yml never runs them - they run locally and in
/// <c>.github/workflows/e2e.yml</c>. Keeping them here avoids giving IntegrationTests a dependency on
/// E2ETests internals.
/// </para>
/// </summary>
public sealed class CredentialResolutionTests
{
    private const string TenantIdKey = "SMTPGATEWAY_E2E_TENANT_ID";
    private const string ClientIdKey = "SMTPGATEWAY_E2E_CLIENT_ID";
    private const string ClientSecretKey = "SMTPGATEWAY_E2E_CLIENT_SECRET";
    private const string SenderMailboxKey = "SMTPGATEWAY_E2E_SENDER_MAILBOX";
    private const string RecipientMailboxKey = "SMTPGATEWAY_E2E_RECIPIENT_MAILBOX";
    private const string RecipientMailboxesKey = "SMTPGATEWAY_E2E_RECIPIENT_MAILBOXES";

    private static Dictionary<string, string> FullSet(string prefix) => new(StringComparer.Ordinal)
    {
        [TenantIdKey] = prefix + "tenant",
        [ClientIdKey] = prefix + "client",
        [ClientSecretKey] = prefix + "secret",
        [SenderMailboxKey] = prefix + "sender@example.test",
        [RecipientMailboxKey] = prefix + "rcpt@example.test",
        [RecipientMailboxesKey] = $"{prefix}a@example.test, {prefix}b@example.test, {prefix}c@example.test",
    };

    private static Func<string, string?> LookupFrom(IReadOnlyDictionary<string, string> values) =>
        key => values.GetValueOrDefault(key);

    [Fact]
    public void EnvFileValue_WinsOverEnvironmentVariable()
    {
        var envFile = FullSet("file-");
        var environment = FullSet("env-");

        var creds = E2ECredentials.Resolve(envFile, LookupFrom(environment));

        Assert.True(creds.Available);
        Assert.Equal("file-tenant", creds.TenantId);
        Assert.Equal("file-client", creds.ClientId);
        Assert.Equal("file-secret", creds.ClientSecret);
        Assert.Equal("file-sender@example.test", creds.SenderMailbox);
        Assert.Equal("file-rcpt@example.test", creds.RecipientMailbox);
        Assert.Equal(
            ["file-a@example.test", "file-b@example.test", "file-c@example.test"],
            creds.RecipientMailboxes);
        Assert.True(creds.HasRecipientMailboxes);
    }

    [Fact]
    public void EnvironmentVariable_UsedWhenEnvFileKeyAbsent()
    {
        var envFile = new Dictionary<string, string>(StringComparer.Ordinal);
        var environment = FullSet("env-");

        var creds = E2ECredentials.Resolve(envFile, LookupFrom(environment));

        Assert.True(creds.Available);
        Assert.Equal("env-tenant", creds.TenantId);
        Assert.Equal("env-client", creds.ClientId);
        Assert.Equal("env-secret", creds.ClientSecret);
        Assert.Equal("env-sender@example.test", creds.SenderMailbox);
        Assert.Equal("env-rcpt@example.test", creds.RecipientMailbox);
        Assert.True(creds.HasRecipientMailboxes);
    }

    [Fact]
    public void EnvironmentVariable_UsedWhenEnvFileValueBlank()
    {
        // Every key is present in the .env dictionary but blank - the environment must win.
        var envFile = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TenantIdKey] = string.Empty,
            [ClientIdKey] = "   ",
            [ClientSecretKey] = string.Empty,
            [SenderMailboxKey] = string.Empty,
            [RecipientMailboxKey] = string.Empty,
        };
        var environment = FullSet("env-");

        var creds = E2ECredentials.Resolve(envFile, LookupFrom(environment));

        Assert.True(creds.Available);
        Assert.Equal("env-tenant", creds.TenantId);
        Assert.Equal("env-client", creds.ClientId);
    }

    [Fact]
    public void PerKeyPrecedence_MixesSourcesIndependently()
    {
        // .env supplies some keys, environment supplies the rest - each key resolves on its own.
        var envFile = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TenantIdKey] = "file-tenant",
            [ClientSecretKey] = "file-secret",
        };
        var environment = FullSet("env-");

        var creds = E2ECredentials.Resolve(envFile, LookupFrom(environment));

        Assert.True(creds.Available);
        Assert.Equal("file-tenant", creds.TenantId);
        Assert.Equal("env-client", creds.ClientId);
        Assert.Equal("file-secret", creds.ClientSecret);
        Assert.Equal("env-sender@example.test", creds.SenderMailbox);
    }

    [Fact]
    public void NeitherSource_NotAvailable()
    {
        var envFile = new Dictionary<string, string>(StringComparer.Ordinal);

        var creds = E2ECredentials.Resolve(envFile, _ => null);

        Assert.False(creds.Available);
        Assert.False(creds.HasRecipientMailboxes);
        Assert.Equal(string.Empty, creds.TenantId);
        Assert.Equal(string.Empty, creds.ClientId);
        Assert.Empty(creds.RecipientMailboxes);
    }
}
