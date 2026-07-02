using System.Text.Json;
using Spectre.Console.Testing;
using SmtpGateway.Admin.Tui;
using SmtpGateway.Core;
using Xunit;

namespace SmtpGateway.IntegrationTests;

/// <summary>
/// End-to-end tests for the 'setup' wizard, driven through the real <see cref="AdminTuiApp"/>
/// command tree with scripted <see cref="TestConsoleInput"/> keystrokes. Every accepted default or
/// first-item selection is a single Enter; typed values use PushTextWithEnter; SelectionPrompt
/// navigation uses DownArrow. The final provider-test offer is always answered 'n' so no live
/// connection is attempted.
/// </summary>
public sealed class WizardCommandTests : IDisposable
{
    private readonly string _root;
    private readonly string _configPath;

    public WizardCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "SmtpGateway.WizardCommandTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _configPath = Path.Combine(_root, "appsettings.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void WriteConfig(string authMode, string? username, string? password, string? endpoint = null, string? host = null, string? tlsMode = null)
    {
        var creds = authMode == "UsernamePassword"
            ? $""", "Username": {JsonSerializer.Serialize(username)}, "Password": {JsonSerializer.Serialize(password)}"""
            : string.Empty;

        File.WriteAllText(
            _configPath,
            $$"""
            {
              "Gateway": {
                "Smtp": { "BindEndpoints": [ {{JsonSerializer.Serialize(endpoint ?? "127.0.0.1:2525")}} ], "MaxRecipients": 42 },
                "SpoolDirectory": {{JsonSerializer.Serialize(Path.Combine(_root, "spool"))}},
                "QueueDatabasePath": {{JsonSerializer.Serialize(Path.Combine(_root, "queue.db"))}},
                "QueueTtl": "5.00:00:00",
                "OutboundProvider": {
                  "Provider": "GenericSmtp",
                  "GenericSmtp": { "Host": {{JsonSerializer.Serialize(host ?? "smtp.example.com")}}, "Port": 587, "TlsMode": {{JsonSerializer.Serialize(tlsMode ?? "StartTlsRequired")}}, "AuthMode": "{{authMode}}"{{creds}} }
                }
              },
              "Logging": { "LogLevel": { "Default": "Information" } }
            }
            """);
    }

    private static void Enter(TestConsoleInput input) => input.PushKey(ConsoleKey.Enter);

    private (int ExitCode, string Output) Run(Action<TestConsoleInput> script) =>
        TuiTestRunner.Run(script, "setup", "--config", _configPath);

    [Fact]
    public void HappyPath_GenericSmtp_AcceptDefaults_WritesValuesAndPreservesUnrelatedKeys()
    {
        WriteConfig("UsernamePassword", "u", "p");

        var (exitCode, output) = Run(i =>
        {
            Enter(i); // page 1: bind endpoint (default)
            Enter(i); // page 1 nav: Next
            Enter(i); // page 2: spool (default)
            Enter(i); // page 2: queue db (default)
            Enter(i); // page 2 nav: Next
            Enter(i); // page 3: provider = GenericSmtp (current first)
            Enter(i); // page 3: host (default)
            Enter(i); // page 3: port (default)
            Enter(i); // page 3: TLS mode (current first)
            Enter(i); // page 3: auth mode = UsernamePassword (current first)
            Enter(i); // page 3: username (default)
            Enter(i); // page 3: password (default)
            Enter(i); // page 3 nav: Next
            Enter(i); // review: Save
            i.PushTextWithEnter("n"); // provider test offer: no
        });

        Assert.Equal(0, exitCode);
        Assert.Contains("Restart required", output);

        var options = GatewayConfigLoader.Load(_configPath);
        Assert.Contains("127.0.0.1:2525", options.Smtp.BindEndpoints);
        Assert.False(options.Smtp.AllowNonLoopbackBind);
        Assert.Equal("GenericSmtp", options.OutboundProvider.Provider);
        Assert.Equal("smtp.example.com", options.OutboundProvider.GenericSmtp!.Host);
        Assert.Equal(AuthMode.UsernamePassword, options.OutboundProvider.GenericSmtp!.AuthMode);
        Assert.Equal("u", options.OutboundProvider.GenericSmtp!.Username);
        Assert.Equal("p", options.OutboundProvider.GenericSmtp!.Password);

        // Unrelated pre-existing keys must survive the rewrite.
        Assert.Equal(42, options.Smtp.MaxRecipients);
        Assert.Equal(TimeSpan.FromDays(5), options.QueueTtl);
        Assert.Contains("Logging", File.ReadAllText(_configPath));
    }

    [Fact]
    public void BackNavigation_PreservesLaterPageEntryAndAppliesChangedEarlierValue()
    {
        WriteConfig("None", null, null);
        var customSpool = Path.Combine(_root, "custom-spool");

        var (exitCode, _) = Run(i =>
        {
            Enter(i);                              // page 1: endpoint (default 127.0.0.1:2525)
            Enter(i);                              // page 1 nav: Next
            i.PushTextWithEnter(customSpool);      // page 2: spool (custom)
            Enter(i);                              // page 2: queue db (default)
            i.PushKey(ConsoleKey.DownArrow);       // page 2 nav: move to Back
            Enter(i);                              // page 2 nav: Back -> page 1
            i.PushTextWithEnter("127.0.0.1:3535"); // page 1: endpoint (changed)
            Enter(i);                              // page 1 nav: Next
            Enter(i);                              // page 2: spool (default now = custom, preserved)
            Enter(i);                              // page 2: queue db (default)
            Enter(i);                              // page 2 nav: Next
            Enter(i);                              // page 3: provider
            Enter(i);                              // page 3: host
            Enter(i);                              // page 3: port
            Enter(i);                              // page 3: TLS mode
            Enter(i);                              // page 3: auth mode = None
            Enter(i);                              // page 3 nav: Next
            Enter(i);                              // review: Save
            i.PushTextWithEnter("n");              // provider test offer: no
        });

        Assert.Equal(0, exitCode);

        var options = GatewayConfigLoader.Load(_configPath);
        Assert.Contains("127.0.0.1:3535", options.Smtp.BindEndpoints);
        Assert.DoesNotContain("127.0.0.1:2525", options.Smtp.BindEndpoints);
        Assert.Equal(customSpool, options.SpoolDirectory);
    }

    [Fact]
    public void NonLoopbackEndpoint_RequiresConfirmation_SetsAllowFlagAndAuthKeys()
    {
        WriteConfig("None", null, null);

        var (exitCode, output) = Run(i =>
        {
            i.PushTextWithEnter("192.168.1.10:2525"); // page 1: non-loopback endpoint
            i.PushTextWithEnter("y");                 // confirm bind non-loopback
            i.PushTextWithEnter("y");                 // confirm set inbound AUTH
            i.PushTextWithEnter("inbounduser");       // AUTH username
            i.PushTextWithEnter("inboundpass");       // AUTH password
            Enter(i);                                 // page 1 nav: Next
            Enter(i);                                 // page 2: spool
            Enter(i);                                 // page 2: queue db
            Enter(i);                                 // page 2 nav: Next
            Enter(i);                                 // page 3: provider
            Enter(i);                                 // page 3: host
            Enter(i);                                 // page 3: port
            Enter(i);                                 // page 3: TLS mode
            Enter(i);                                 // page 3: auth mode = None
            Enter(i);                                 // page 3 nav: Next
            Enter(i);                                 // review: Save
            i.PushTextWithEnter("n");                 // provider test offer: no
        });

        Assert.Equal(0, exitCode);
        Assert.Contains("Security warning", output);

        var options = GatewayConfigLoader.Load(_configPath);
        Assert.Contains("192.168.1.10:2525", options.Smtp.BindEndpoints);
        Assert.True(options.Smtp.AllowNonLoopbackBind);
        Assert.Equal("inbounduser", options.Smtp.AuthUsername);
        Assert.Equal("inboundpass", options.Smtp.AuthPassword);
    }

    [Fact]
    public void CancelOnReview_WritesNothing()
    {
        WriteConfig("None", null, null);
        var before = File.ReadAllText(_configPath);

        var (exitCode, _) = Run(i =>
        {
            Enter(i);                        // page 1: endpoint
            Enter(i);                        // page 1 nav: Next
            Enter(i);                        // page 2: spool
            Enter(i);                        // page 2: queue db
            Enter(i);                        // page 2 nav: Next
            Enter(i);                        // page 3: provider
            Enter(i);                        // page 3: host
            Enter(i);                        // page 3: port
            Enter(i);                        // page 3: TLS mode
            Enter(i);                        // page 3: auth mode = None
            Enter(i);                        // page 3 nav: Next
            for (var d = 0; d < 4; d++)
            {
                i.PushKey(ConsoleKey.DownArrow); // review: move to Cancel (5th item)
            }

            Enter(i);                        // review: Cancel
        });

        Assert.Equal(0, exitCode);
        Assert.Equal(before, File.ReadAllText(_configPath));
    }

    [Fact]
    public void MissingFile_CancelOnReview_LeavesFileAbsent()
    {
        // No config written: first-install path where the file does not yet exist.
        var (exitCode, _) = Run(i =>
        {
            i.PushTextWithEnter("127.0.0.1:2525"); // page 1: endpoint (no prefill default is the built-in)
            Enter(i);                              // page 1 nav: Next
            Enter(i);                              // page 2: spool (built-in default)
            Enter(i);                              // page 2: queue db (built-in default)
            Enter(i);                              // page 2 nav: Next
            Enter(i);                              // page 3: provider = GenericSmtp
            i.PushTextWithEnter("smtp.relay.test"); // page 3: host (no default -> required)
            Enter(i);                              // page 3: port
            Enter(i);                              // page 3: TLS mode
            Enter(i);                              // page 3: auth mode = None
            Enter(i);                              // page 3 nav: Next
            for (var d = 0; d < 4; d++)
            {
                i.PushKey(ConsoleKey.DownArrow);   // review: move to Cancel
            }

            Enter(i);                              // review: Cancel
        });

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(_configPath));
    }

    [Fact]
    public void Prefill_ExistingValuesAppearAsDefaults_AndRoundTripOnSave()
    {
        WriteConfig("None", null, null, endpoint: "127.0.0.1:9099", host: "relay.prefill.test", tlsMode: "SslOnConnect");

        var (exitCode, _) = Run(i =>
        {
            Enter(i);                 // page 1: endpoint (prefilled default)
            Enter(i);                 // page 1 nav: Next
            Enter(i);                 // page 2: spool (prefilled default)
            Enter(i);                 // page 2: queue db (prefilled default)
            Enter(i);                 // page 2 nav: Next
            Enter(i);                 // page 3: provider (prefilled)
            Enter(i);                 // page 3: host (prefilled default)
            Enter(i);                 // page 3: port (prefilled default 587)
            Enter(i);                 // page 3: TLS mode (SslOnConnect current-first)
            Enter(i);                 // page 3: auth mode = None (current-first)
            Enter(i);                 // page 3 nav: Next
            Enter(i);                 // review: Save
            i.PushTextWithEnter("n"); // provider test offer: no
        });

        Assert.Equal(0, exitCode);

        var options = GatewayConfigLoader.Load(_configPath);
        Assert.Contains("127.0.0.1:9099", options.Smtp.BindEndpoints);
        Assert.Equal(Path.Combine(_root, "spool"), options.SpoolDirectory);
        Assert.Equal(Path.Combine(_root, "queue.db"), options.QueueDatabasePath);
        Assert.Equal("relay.prefill.test", options.OutboundProvider.GenericSmtp!.Host);
        Assert.Equal(SmtpTlsMode.SslOnConnect, options.OutboundProvider.GenericSmtp!.TlsMode);
        Assert.Equal(AuthMode.None, options.OutboundProvider.GenericSmtp!.AuthMode);
    }
}
