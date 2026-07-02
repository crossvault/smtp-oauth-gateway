namespace SmtpGateway.E2ETests;

/// <summary>
/// The live-E2E credentials, loaded once. For each key the value comes from the repo-root
/// <c>.env</c> file (gitignored) when present and non-blank, otherwise from the equally-named
/// process environment variable (so GitHub Actions can supply them as secrets). When neither
/// source has a value, <see cref="Available"/> is <c>false</c> and every live test skips - so CI
/// (which has no <c>.env</c> and no secrets) stays green without ever making a network call.
/// Values are never logged, printed, or embedded in any assertion message.
/// </summary>
internal sealed class E2ECredentials
{
    private const string TenantIdKey = "SMTPGATEWAY_E2E_TENANT_ID";
    private const string ClientIdKey = "SMTPGATEWAY_E2E_CLIENT_ID";
    private const string ClientSecretKey = "SMTPGATEWAY_E2E_CLIENT_SECRET";
    private const string SenderMailboxKey = "SMTPGATEWAY_E2E_SENDER_MAILBOX";
    private const string RecipientMailboxKey = "SMTPGATEWAY_E2E_RECIPIENT_MAILBOX";
    private const string RecipientMailboxesKey = "SMTPGATEWAY_E2E_RECIPIENT_MAILBOXES";

    /// <summary>Minimum number of extra recipient mailboxes the CC/BCC and multi-recipient tests need.</summary>
    private const int MinimumRecipientMailboxes = 3;

    /// <summary>Shared instance, loaded exactly once for the whole test run.</summary>
    public static E2ECredentials Shared { get; } = Load();

    public bool Available { get; }

    public string TenantId { get; }

    public string ClientId { get; }

    public string ClientSecret { get; }

    public string SenderMailbox { get; }

    public string RecipientMailbox { get; }

    /// <summary>
    /// The extra sandbox mailboxes parsed from <c>SMTPGATEWAY_E2E_RECIPIENT_MAILBOXES</c>
    /// (comma-separated). Empty when the key is absent. Never logged or echoed.
    /// </summary>
    public IReadOnlyList<string> RecipientMailboxes { get; }

    /// <summary>
    /// True when the base credentials are present AND at least
    /// <see cref="MinimumRecipientMailboxes"/> extra recipient mailboxes are configured - the
    /// precondition for the CC/BCC and multi-recipient live tests.
    /// </summary>
    public bool HasRecipientMailboxes => Available && RecipientMailboxes.Count >= MinimumRecipientMailboxes;

    private E2ECredentials(
        bool available,
        string tenantId,
        string clientId,
        string clientSecret,
        string senderMailbox,
        string recipientMailbox,
        IReadOnlyList<string> recipientMailboxes)
    {
        Available = available;
        TenantId = tenantId;
        ClientId = clientId;
        ClientSecret = clientSecret;
        SenderMailbox = senderMailbox;
        RecipientMailbox = recipientMailbox;
        RecipientMailboxes = recipientMailboxes;
    }

    private static E2ECredentials Load() => Resolve(EnvFile.TryLoad(), Environment.GetEnvironmentVariable);

    /// <summary>
    /// Testable seam for the .env-over-environment precedence rule. For each key a non-blank value
    /// from <paramref name="envFile"/> wins; otherwise the equally-named entry from
    /// <paramref name="environmentLookup"/> is used; if neither has a value the field is empty and
    /// <see cref="Available"/> stays false. Unit tests call this with a fake dictionary and lookup
    /// delegate, so the precedence logic is covered without a real <c>.env</c> or the developer's
    /// live tenant.
    /// <para>
    /// Tradeoff: those precedence tests live in this E2ETests project, which
    /// <c>.github/workflows/ci.yml</c> deliberately excludes (an all-skipped MTP run exits 8). So
    /// ci.yml never exercises them - they run locally and in <c>.github/workflows/e2e.yml</c>
    /// instead. They stay here rather than moving to IntegrationTests, which must not take a
    /// dependency on E2ETests internals.
    /// </para>
    /// </summary>
    internal static E2ECredentials Resolve(
        IReadOnlyDictionary<string, string> envFile,
        Func<string, string?> environmentLookup)
    {
        var tenantId = ResolveValue(envFile, environmentLookup, TenantIdKey);
        var clientId = ResolveValue(envFile, environmentLookup, ClientIdKey);
        var clientSecret = ResolveValue(envFile, environmentLookup, ClientSecretKey);
        var senderMailbox = ResolveValue(envFile, environmentLookup, SenderMailboxKey);
        var recipientMailbox = ResolveValue(envFile, environmentLookup, RecipientMailboxKey);
        var recipientMailboxes = ParseList(ResolveValue(envFile, environmentLookup, RecipientMailboxesKey));

        var available = !string.IsNullOrWhiteSpace(tenantId)
            && !string.IsNullOrWhiteSpace(clientId)
            && !string.IsNullOrWhiteSpace(clientSecret)
            && !string.IsNullOrWhiteSpace(senderMailbox)
            && !string.IsNullOrWhiteSpace(recipientMailbox);

        return new E2ECredentials(
            available, tenantId, clientId, clientSecret, senderMailbox, recipientMailbox, recipientMailboxes);
    }

    private static string ResolveValue(
        IReadOnlyDictionary<string, string> envFile,
        Func<string, string?> environmentLookup,
        string key)
    {
        var fromFile = envFile.GetValueOrDefault(key, string.Empty);
        return string.IsNullOrWhiteSpace(fromFile) ? environmentLookup(key) ?? string.Empty : fromFile;
    }

    private static IReadOnlyList<string> ParseList(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
