using System.Net;
using System.Text.Json.Nodes;
using Spectre.Console;
using Spectre.Console.Cli;
using SmtpGateway.Core;
using SmtpGateway.Infrastructure;

namespace SmtpGateway.Admin.Tui.Commands;

/// <summary>
/// 'setup [--config &lt;PATH&gt;]': a first-install interactive wizard that walks the operator through
/// the minimum settings needed to run the gateway (inbound bind endpoints, storage paths, outbound
/// provider) and writes them to appsettings.json via <see cref="ConfigDocument"/> - preserving every
/// unrelated key exactly like 'config set'. Everything else (TTLs, rate limits, ...) stays editable
/// via 'config set'; this wizard deliberately covers only the first-run essentials.
///
/// Navigation is a tiny three-page state machine (Inbound -&gt; Storage -&gt; Outbound) plus a final
/// review page; the operator can go Back/Next between pages or Cancel at any point. Only choosing
/// Save on the review page writes anything. On save it re-runs the same validation as
/// 'config validate' (reported, never rolled back) and offers the warnings-only 'provider test'
/// connectivity probe.
/// </summary>
public sealed class WizardCommand : AsyncCommand<GatewayCommandSettings>
{
    private const string DefaultBindEndpoint = "127.0.0.1:2525";
    private const string DefaultSpoolDirectory = @"C:\ProgramData\SmtpGateway\spool";
    private const string DefaultQueueDatabasePath = @"C:\ProgramData\SmtpGateway\queue.db";

    private const string NavNext = "Next";
    private const string NavBack = "Back";
    private const string NavCancel = "Cancel";

    private const int InboundPage = 0;
    private const int StoragePage = 1;
    private const int OutboundPage = 2;
    private const int ReviewPage = 3;

    private enum Nav
    {
        Next,
        Back,
        Cancel,
        Save,
        Goto,
    }

    private readonly record struct NavResult(Nav Kind, int Target = 0);

    protected override Task<int> ExecuteAsync(CommandContext context, GatewayCommandSettings settings, CancellationToken cancellationToken) =>
        RunAsync(settings.ConfigPath, cancellationToken);

    /// <summary>
    /// Runs the wizard flow directly (bypassing Spectre.Console.Cli), so the interactive shell's
    /// "First-time setup" entry can drive the exact same page state machine the 'setup' command uses.
    /// </summary>
    public static async Task<int> RunAsync(string? configPath, CancellationToken cancellationToken)
    {
        var path = GatewayConfigLoader.ResolvePath(configPath);
        var state = new WizardState();

        if (File.Exists(path))
        {
            JsonObject root;
            try
            {
                root = ConfigDocument.LoadRoot(path);
            }
            catch (Exception ex)
            {
                // An existing-but-unreadable config file is a genuine usage error, not a wizard
                // step the operator can proceed past - fail with a non-zero exit like the other
                // commands do on a bad --config file.
                AnsiConsole.MarkupLineInterpolated($"[red]Failed to read existing configuration '{path}': {ex.Message}[/]");
                return 1;
            }

            Prefill(state, root);
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]'{path}' already exists. Its current values are shown as defaults below, and the whole file will be rewritten on Save (no backup is kept).[/]");
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[grey]'{path}' does not exist yet; it will be created on Save.[/]");
        }

        var page = InboundPage;
        while (true)
        {
            var nav = page switch
            {
                InboundPage => RunInboundPage(state),
                StoragePage => RunStoragePage(state),
                OutboundPage => RunOutboundPage(state),
                _ => RunReviewPage(state),
            };

            switch (nav.Kind)
            {
                case Nav.Cancel:
                    AnsiConsole.MarkupLine("[yellow]Setup cancelled. Nothing was written.[/]");
                    return 0;
                case Nav.Save:
                    return await SaveAsync(state, path, cancellationToken).ConfigureAwait(false);
                case Nav.Back:
                    page = Math.Max(InboundPage, page - 1);
                    break;
                case Nav.Goto:
                    page = nav.Target;
                    break;
                default:
                    page += 1;
                    break;
            }
        }
    }

    // --- Page 1: inbound listening ------------------------------------------------------------

    private static NavResult RunInboundPage(WizardState state)
    {
        AnsiConsole.Write(new Rule("[bold]Step 1 of 3: Inbound listening[/]").LeftJustified());

        while (true)
        {
            var raw = AskText("SMTP inbound bind endpoint(s), comma-separated (IP:port)", state.BindEndpoints);

            List<string> endpoints;
            try
            {
                endpoints = ParseEndpoints(raw);
            }
            catch (FormatException ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
                continue;
            }

            if (endpoints.Any(IsNonLoopbackEndpoint))
            {
                AnsiConsole.MarkupLine(
                    "[yellow]Security warning:[/] one or more endpoints are non-loopback or wildcard addresses, " +
                    "making the inbound listener reachable from the network. The inbound listener has no STARTTLS.");

                if (!AnsiConsole.Confirm("Bind a network-reachable endpoint and set AllowNonLoopbackBind=true?", false))
                {
                    // Declined - let the operator enter a different (e.g. loopback) endpoint.
                    continue;
                }

                state.AllowNonLoopbackBind = true;

                AnsiConsole.MarkupLine(
                    "[yellow]Inbound AUTH is strongly recommended for a network-reachable listener.[/] " +
                    "Note: credentials cross the network in cleartext because the inbound listener has no STARTTLS.");

                if (AnsiConsole.Confirm("Set inbound AUTH username/password now?", true))
                {
                    state.AuthUsername = AskText("Inbound AUTH username", state.AuthUsername);
                    state.AuthPassword = AskText("Inbound AUTH password (shown in cleartext)", state.AuthPassword);
                }
            }
            else
            {
                state.AllowNonLoopbackBind = false;
            }

            state.BindEndpoints = string.Join(", ", endpoints);
            break;
        }

        return AskNavigation(InboundPage);
    }

    // --- Page 2: storage ----------------------------------------------------------------------

    private static NavResult RunStoragePage(WizardState state)
    {
        AnsiConsole.Write(new Rule("[bold]Step 2 of 3: Storage[/]").LeftJustified());

        state.SpoolDirectory = AskText("Spool directory (raw-MIME file spool)", state.SpoolDirectory);
        state.QueueDatabasePath = AskText("Queue database path (SQLite file)", state.QueueDatabasePath);

        return AskNavigation(StoragePage);
    }

    // --- Page 3: outbound provider ------------------------------------------------------------

    private static NavResult RunOutboundPage(WizardState state)
    {
        AnsiConsole.Write(new Rule("[bold]Step 3 of 3: Outbound provider[/]").LeftJustified());

        state.Provider = AskChoice(
            "Outbound provider",
            state.Provider,
            [nameof(OutboundProviderKind.GenericSmtp), nameof(OutboundProviderKind.M365Oauth), nameof(OutboundProviderKind.Graph)]);

        if (state.Provider == nameof(OutboundProviderKind.GenericSmtp))
        {
            state.GenericHost = AskText("SMTP relay host", state.GenericHost);
            state.GenericPort = AskPort("SMTP relay port", state.GenericPort);
            state.GenericTlsMode = AskEnum("TLS mode", state.GenericTlsMode);
            state.GenericAuthMode = ParseAuthMode(AskChoice(
                "Authentication mode",
                state.GenericAuthMode.ToString(),
                [nameof(AuthMode.None), nameof(AuthMode.UsernamePassword)]));

            if (state.GenericAuthMode == AuthMode.UsernamePassword)
            {
                state.GenericUsername = AskText("SMTP username", state.GenericUsername);
                state.GenericPassword = AskText("SMTP password (shown in cleartext)", state.GenericPassword);
            }
        }
        else
        {
            // M365Oauth and Graph share the same MSAL client-credentials field set.
            state.TenantId = AskText("Tenant ID", state.TenantId);
            state.ClientId = AskText("Client ID", state.ClientId);
            state.ClientSecret = AskText("Client secret (shown in cleartext)", state.ClientSecret);
            state.Mailbox = AskText("Sender mailbox", state.Mailbox);
        }

        return AskNavigation(OutboundPage);
    }

    // --- Review -------------------------------------------------------------------------------

    private static NavResult RunReviewPage(WizardState state)
    {
        AnsiConsole.Write(new Rule("[bold]Review[/]").LeftJustified());

        var table = new Table().Title("Configuration to be written");
        table.AddColumn("Setting");
        table.AddColumn("Value");

        foreach (var (label, value) in DescribeState(state))
        {
            table.AddRow(Markup.Escape(label), Markup.Escape(value));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]Only the settings above are managed by this wizard. Everything else (TTL, rate limits, ...) stays editable via 'config set'.[/]");

        const string save = "Save";
        const string backInbound = "Go back to: Inbound listening";
        const string backStorage = "Go back to: Storage";
        const string backOutbound = "Go back to: Outbound provider";
        const string cancel = "Cancel (write nothing)";

        var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("What next?")
            .AddChoices(save, backInbound, backStorage, backOutbound, cancel));

        return choice switch
        {
            save => new NavResult(Nav.Save),
            backInbound => new NavResult(Nav.Goto, InboundPage),
            backStorage => new NavResult(Nav.Goto, StoragePage),
            backOutbound => new NavResult(Nav.Goto, OutboundPage),
            _ => new NavResult(Nav.Cancel),
        };
    }

    private static IEnumerable<(string Label, string Value)> DescribeState(WizardState state)
    {
        yield return ("Smtp:BindEndpoints", state.BindEndpoints);
        yield return ("Smtp:AllowNonLoopbackBind", state.AllowNonLoopbackBind ? "true" : "false");
        yield return ("Smtp:AuthUsername", state.AuthUsername ?? "(none)");
        yield return ("Smtp:AuthPassword", state.AuthPassword ?? "(none)");
        yield return ("SpoolDirectory", state.SpoolDirectory);
        yield return ("QueueDatabasePath", state.QueueDatabasePath);
        yield return ("OutboundProvider:Provider", state.Provider);

        if (state.Provider == nameof(OutboundProviderKind.GenericSmtp))
        {
            yield return ("GenericSmtp:Host", state.GenericHost);
            yield return ("GenericSmtp:Port", state.GenericPort);
            yield return ("GenericSmtp:TlsMode", state.GenericTlsMode.ToString());
            yield return ("GenericSmtp:AuthMode", state.GenericAuthMode.ToString());
            if (state.GenericAuthMode == AuthMode.UsernamePassword)
            {
                yield return ("GenericSmtp:Username", state.GenericUsername ?? string.Empty);
                yield return ("GenericSmtp:Password", state.GenericPassword ?? string.Empty);
            }
        }
        else
        {
            yield return ($"{state.Provider}:TenantId", state.TenantId);
            yield return ($"{state.Provider}:ClientId", state.ClientId);
            yield return ($"{state.Provider}:ClientSecret", state.ClientSecret);
            yield return ($"{state.Provider}:Mailbox", state.Mailbox);
        }
    }

    // --- Save ---------------------------------------------------------------------------------

    private static async Task<int> SaveAsync(WizardState state, string path, CancellationToken cancellationToken)
    {
        JsonObject root;
        try
        {
            root = File.Exists(path) ? ConfigDocument.LoadRoot(path) : new JsonObject();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Failed to load configuration for writing: {ex.Message}[/]");
            return 1;
        }

        var gateway = ConfigDocument.GetOrCreateGatewaySection(root);

        // BindEndpoints is a JSON array, which ConfigDocument.SetPath (leaf scalars only) cannot
        // express, so it is written directly, replacing whatever was there while leaving the rest
        // of the Smtp section untouched.
        var smtp = GetOrCreateChild(gateway, "Smtp");
        var endpointsArray = new JsonArray();
        foreach (var endpoint in ParseEndpoints(state.BindEndpoints))
        {
            endpointsArray.Add(JsonValue.Create(endpoint));
        }

        smtp["BindEndpoints"] = endpointsArray;
        ConfigDocument.SetPath(gateway, "Smtp:AllowNonLoopbackBind", state.AllowNonLoopbackBind ? "true" : "false");

        if (!string.IsNullOrEmpty(state.AuthUsername) && !string.IsNullOrEmpty(state.AuthPassword))
        {
            ConfigDocument.SetPath(gateway, "Smtp:AuthUsername", state.AuthUsername);
            ConfigDocument.SetPath(gateway, "Smtp:AuthPassword", state.AuthPassword);
        }

        ConfigDocument.SetPath(gateway, "SpoolDirectory", state.SpoolDirectory);
        ConfigDocument.SetPath(gateway, "QueueDatabasePath", state.QueueDatabasePath);
        ConfigDocument.SetPath(gateway, "OutboundProvider:Provider", state.Provider);

        if (state.Provider == nameof(OutboundProviderKind.GenericSmtp))
        {
            ConfigDocument.SetPath(gateway, "OutboundProvider:GenericSmtp:Host", state.GenericHost);
            ConfigDocument.SetPath(gateway, "OutboundProvider:GenericSmtp:Port", state.GenericPort);
            ConfigDocument.SetPath(gateway, "OutboundProvider:GenericSmtp:TlsMode", state.GenericTlsMode.ToString());
            ConfigDocument.SetPath(gateway, "OutboundProvider:GenericSmtp:AuthMode", state.GenericAuthMode.ToString());
            if (state.GenericAuthMode == AuthMode.UsernamePassword)
            {
                ConfigDocument.SetPath(gateway, "OutboundProvider:GenericSmtp:Username", state.GenericUsername ?? string.Empty);
                ConfigDocument.SetPath(gateway, "OutboundProvider:GenericSmtp:Password", state.GenericPassword ?? string.Empty);
            }
        }
        else
        {
            var section = $"OutboundProvider:{state.Provider}";
            ConfigDocument.SetPath(gateway, $"{section}:TenantId", state.TenantId);
            ConfigDocument.SetPath(gateway, $"{section}:ClientId", state.ClientId);
            ConfigDocument.SetPath(gateway, $"{section}:ClientSecret", state.ClientSecret);
            ConfigDocument.SetPath(gateway, $"{section}:Mailbox", state.Mailbox);
        }

        try
        {
            ConfigDocument.Save(root, path);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Failed to write configuration '{path}': {ex.Message}[/]");
            return 1;
        }

        AnsiConsole.MarkupLineInterpolated($"[green]Configuration written to '{path}'.[/]");

        ReportValidation(path);

        if (AnsiConsole.Confirm("Run a live outbound provider connectivity test now?", false))
        {
            GatewayOptions options;
            try
            {
                options = GatewayConfigLoader.Load(path);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]Warning: could not load configuration for the provider test: {ex.Message}[/]");
                options = null!;
            }

            if (options is not null)
            {
                await ProviderConnectivityCheck.RunAsync(options.OutboundProvider, ProviderConnectivityCheck.DefaultTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            }
        }

        AnsiConsole.MarkupLine("[yellow]Restart required: the running service must be restarted to pick up this configuration.[/]");
        return 0;
    }

    /// <summary>
    /// Best-effort post-write validation report, mirroring 'config validate'. The write has already
    /// happened - a bad configuration is reported clearly but never rolled back, matching the
    /// "appsettings.json is the source of truth, no rollback" decision.
    /// </summary>
    private static void ReportValidation(string path)
    {
        try
        {
            var options = GatewayConfigLoader.Load(path);
            GatewayOptionsValidator.Validate(options);
            OutboundProviderFactory.Create(options.OutboundProvider);
            AnsiConsole.MarkupLine("[green]Configuration is valid.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]Warning: the written configuration is invalid: {ex.Message}[/]");
        }
    }

    // --- Prefill ------------------------------------------------------------------------------

    private static void Prefill(WizardState state, JsonObject root)
    {
        if (root[ConfigDocument.GatewaySectionName] is not JsonObject gateway)
        {
            return;
        }

        if (gateway["Smtp"] is JsonObject smtp)
        {
            if (smtp["BindEndpoints"] is JsonArray endpoints && endpoints.Count > 0)
            {
                state.BindEndpoints = string.Join(", ", endpoints.Select(e => e?.ToString() ?? string.Empty));
            }

            state.AllowNonLoopbackBind = ParseBool(GetLeaf(gateway, "Smtp:AllowNonLoopbackBind"), state.AllowNonLoopbackBind);
            state.AuthUsername = NullIfEmpty(GetLeaf(gateway, "Smtp:AuthUsername"));
            state.AuthPassword = NullIfEmpty(GetLeaf(gateway, "Smtp:AuthPassword"));
        }

        state.SpoolDirectory = GetLeaf(gateway, "SpoolDirectory") is { Length: > 0 } spool ? spool : state.SpoolDirectory;
        state.QueueDatabasePath = GetLeaf(gateway, "QueueDatabasePath") is { Length: > 0 } queue ? queue : state.QueueDatabasePath;

        var provider = GetLeaf(gateway, "OutboundProvider:Provider");
        if (!string.IsNullOrWhiteSpace(provider))
        {
            state.Provider = provider;
        }

        state.GenericHost = GetLeaf(gateway, "OutboundProvider:GenericSmtp:Host") ?? state.GenericHost;
        state.GenericPort = GetLeaf(gateway, "OutboundProvider:GenericSmtp:Port") is { Length: > 0 } port ? port : state.GenericPort;
        state.GenericTlsMode = ParseEnum(GetLeaf(gateway, "OutboundProvider:GenericSmtp:TlsMode"), state.GenericTlsMode);
        state.GenericAuthMode = ParseEnum(GetLeaf(gateway, "OutboundProvider:GenericSmtp:AuthMode"), state.GenericAuthMode);
        state.GenericUsername = NullIfEmpty(GetLeaf(gateway, "OutboundProvider:GenericSmtp:Username"));
        state.GenericPassword = NullIfEmpty(GetLeaf(gateway, "OutboundProvider:GenericSmtp:Password"));

        // Prefill the shared OAuth fields from whichever section matches the selected provider.
        var oauthSection = state.Provider == nameof(OutboundProviderKind.Graph)
            ? "OutboundProvider:Graph"
            : "OutboundProvider:M365Oauth";
        state.TenantId = GetLeaf(gateway, $"{oauthSection}:TenantId") ?? state.TenantId;
        state.ClientId = GetLeaf(gateway, $"{oauthSection}:ClientId") ?? state.ClientId;
        state.ClientSecret = GetLeaf(gateway, $"{oauthSection}:ClientSecret") ?? state.ClientSecret;
        state.Mailbox = GetLeaf(gateway, $"{oauthSection}:Mailbox") ?? state.Mailbox;
    }

    private static string? GetLeaf(JsonObject gateway, string dottedPath) => ConfigDocument.GetPath(gateway, dottedPath);

    // --- Prompt helpers -----------------------------------------------------------------------

    private static string AskText(string label, string? current)
    {
        var prompt = new TextPrompt<string>($"{label}:");
        if (!string.IsNullOrEmpty(current))
        {
            prompt.DefaultValue(current);
        }

        return AnsiConsole.Prompt(prompt);
    }

    private static string AskPort(string label, string current)
    {
        var defaultValue = int.TryParse(current, out var parsed) ? parsed : 587;
        var value = AnsiConsole.Prompt(new TextPrompt<int>($"{label}:")
            .DefaultValue(defaultValue)
            .Validate(p => p is >= 1 and <= 65535
                ? ValidationResult.Success()
                : ValidationResult.Error("Port must be between 1 and 65535.")));
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string AskChoice(string label, string current, IReadOnlyList<string> choices)
    {
        // Put the current/prefilled value first so pressing Enter keeps it (SelectionPrompt has no
        // pre-selected-default concept in this version).
        var ordered = new List<string>(choices);
        if (ordered.Remove(current))
        {
            ordered.Insert(0, current);
        }

        return AnsiConsole.Prompt(new SelectionPrompt<string>().Title($"{label}:").AddChoices(ordered));
    }

    private static TEnum AskEnum<TEnum>(string label, TEnum current) where TEnum : struct, Enum =>
        Enum.Parse<TEnum>(AskChoice(label, current.ToString(), Enum.GetNames<TEnum>()));

    private static NavResult AskNavigation(int page)
    {
        var choices = new List<string> { NavNext };
        if (page > InboundPage)
        {
            choices.Add(NavBack);
        }

        choices.Add(NavCancel);

        var choice = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Proceed?").AddChoices(choices));
        return choice switch
        {
            NavBack => new NavResult(Nav.Back),
            NavCancel => new NavResult(Nav.Cancel),
            _ => new NavResult(Nav.Next),
        };
    }

    // --- Parsing helpers ----------------------------------------------------------------------

    private static List<string> ParseEndpoints(string raw)
    {
        var endpoints = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (endpoints.Count == 0)
        {
            throw new FormatException("At least one bind endpoint is required, e.g. '127.0.0.1:2525'.");
        }

        // Reject malformed endpoints up front (reusing the same parser the service uses at startup)
        // so the operator fixes them here rather than seeing an obscure failure after Save.
        foreach (var endpoint in endpoints)
        {
            _ = SmtpBindEndpointParser.Parse(endpoint);
        }

        return endpoints;
    }

    private static bool IsNonLoopbackEndpoint(string endpoint)
    {
        var parsed = SmtpBindEndpointParser.Parse(endpoint);
        return !IPAddress.IsLoopback(parsed.Address);
    }

    private static AuthMode ParseAuthMode(string value) =>
        Enum.TryParse<AuthMode>(value, ignoreCase: true, out var mode) ? mode : AuthMode.None;

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : fallback;

    private static bool ParseBool(string? value, bool fallback) =>
        bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static JsonObject GetOrCreateChild(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    /// <summary>Mutable collector for everything the wizard gathers across its pages.</summary>
    private sealed class WizardState
    {
        public string BindEndpoints { get; set; } = DefaultBindEndpoint;

        public bool AllowNonLoopbackBind { get; set; }

        public string? AuthUsername { get; set; }

        public string? AuthPassword { get; set; }

        public string SpoolDirectory { get; set; } = DefaultSpoolDirectory;

        public string QueueDatabasePath { get; set; } = DefaultQueueDatabasePath;

        public string Provider { get; set; } = nameof(OutboundProviderKind.GenericSmtp);

        public string GenericHost { get; set; } = string.Empty;

        public string GenericPort { get; set; } = "587";

        public SmtpTlsMode GenericTlsMode { get; set; } = SmtpTlsMode.StartTlsRequired;

        public AuthMode GenericAuthMode { get; set; } = AuthMode.None;

        public string? GenericUsername { get; set; }

        public string? GenericPassword { get; set; }

        public string TenantId { get; set; } = string.Empty;

        public string ClientId { get; set; } = string.Empty;

        public string ClientSecret { get; set; } = string.Empty;

        public string Mailbox { get; set; } = string.Empty;
    }
}
