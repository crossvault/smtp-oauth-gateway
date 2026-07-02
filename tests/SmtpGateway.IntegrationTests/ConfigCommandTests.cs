using Spectre.Console;
using Spectre.Console.Cli.Testing;
using SmtpGateway.Admin.Tui;
using Xunit;

namespace SmtpGateway.IntegrationTests;

/// <summary>
/// End-to-end command-wiring tests for 'config show'/'config set'/'config validate', run through
/// the real <see cref="AdminTuiApp"/> command tree against a real temp appsettings.json file - no
/// mocking of <see cref="ConfigDocument"/> or <see cref="GatewayConfigLoader"/>.
/// </summary>
public sealed class ConfigCommandTests : IDisposable
{
    private readonly string _root;
    private readonly string _configPath;

    public ConfigCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "SmtpGateway.ConfigCommandTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _configPath = Path.Combine(_root, "appsettings.json");

        File.WriteAllText(
            _configPath,
            $$"""
            {
              "Gateway": {
                "Smtp": { "BindEndpoints": [ "127.0.0.1:25901" ] },
                "SpoolDirectory": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(_root, "spool"))}},
                "QueueDatabasePath": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(_root, "queue.db"))}},
                "OutboundProvider": {
                  "Provider": "GenericSmtp",
                  "GenericSmtp": { "Host": "smtp.example.com", "Port": 587, "AuthMode": "UsernamePassword", "Username": "u", "Password": "p" }
                }
              },
              "Logging": { "LogLevel": { "Default": "Information" } }
            }
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static (int ExitCode, string Output) RunCommand(params string[] args) => TuiTestRunner.Run(args);

    [Fact]
    public void ConfigShow_PrintsDottedPathsAndCleartextSecrets()
    {
        var result = RunCommand("config", "show", "--config", _configPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("OutboundProvider:GenericSmtp:Host", result.Output);
        Assert.Contains("smtp.example.com", result.Output);
        Assert.Contains("OutboundProvider:GenericSmtp:Password", result.Output);
        // Secrets are shown in cleartext by explicit product decision - never redacted/masked.
        Assert.Contains("p", result.Output);
        Assert.DoesNotContain("****", result.Output);
    }

    [Fact]
    public void ConfigSet_UpdatesValueAndPreservesUnrelatedKeys()
    {
        var result = RunCommand("config", "set", "Smtp:MaxRecipients", "250", "--config", _configPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Restart required", result.Output);

        var options = GatewayConfigLoader.Load(_configPath);
        Assert.Equal(250, options.Smtp.MaxRecipients);
        // Unrelated existing keys must survive the write untouched.
        Assert.Equal("GenericSmtp", options.OutboundProvider.Provider);
        Assert.Equal("smtp.example.com", options.OutboundProvider.GenericSmtp!.Host);
    }

    [Fact]
    public void ConfigSet_NestedProviderSecretPath_RoundTrips()
    {
        var result = RunCommand("config", "set", "OutboundProvider:GenericSmtp:Password", "newSecret!", "--config", _configPath);

        Assert.Equal(0, result.ExitCode);

        var options = GatewayConfigLoader.Load(_configPath);
        Assert.Equal("newSecret!", options.OutboundProvider.GenericSmtp!.Password);
    }

    [Fact]
    public void ConfigSet_MalformedPath_FailsClearlyWithoutCorruptingFile()
    {
        var before = File.ReadAllText(_configPath);

        var result = RunCommand("config", "set", "OutboundProvider:Provider:Sub", "value", "--config", _configPath);

        Assert.NotEqual(0, result.ExitCode);

        var after = File.ReadAllText(_configPath);
        Assert.Equal(before, after);
    }

    [Fact]
    public void ConfigValidate_ValidConfig_ReportsSuccess()
    {
        var result = RunCommand("config", "validate", "--config", _configPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("valid", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfigValidate_IncompleteConfig_ReportsClearErrorWithoutThrowing()
    {
        var incompletePath = Path.Combine(_root, "incomplete-appsettings.json");
        File.WriteAllText(incompletePath, """{ "Gateway": { "OutboundProvider": { "Provider": "GenericSmtp" } } }""");

        var result = RunCommand("config", "validate", "--config", incompletePath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Output));
    }
}
