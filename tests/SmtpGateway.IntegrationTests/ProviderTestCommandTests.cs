using Spectre.Console;
using Spectre.Console.Cli.Testing;
using SmtpGateway.Admin.Tui;
using Xunit;

namespace SmtpGateway.IntegrationTests;

/// <summary>
/// End-to-end tests for 'provider test', run through the real <see cref="AdminTuiApp"/> command
/// tree against a real (fake) SMTP server. The central assertion across every case here, including
/// the failure case, is that the command NEVER exits non-zero and NEVER throws - a provider
/// validation failure is a warning only, per the documented product decision.
/// </summary>
public sealed class ProviderTestCommandTests : IDisposable
{
    private readonly string _root;

    public ProviderTestCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "SmtpGateway.ProviderTestCommandTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteConfig(int port)
    {
        var configPath = Path.Combine(_root, "appsettings.json");
        File.WriteAllText(
            configPath,
            $$"""
            {
              "Gateway": {
                "SpoolDirectory": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(_root, "spool"))}},
                "QueueDatabasePath": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(_root, "queue.db"))}},
                "OutboundProvider": {
                  "Provider": "GenericSmtp",
                  "GenericSmtp": { "Host": "127.0.0.1", "Port": {{port}}, "TlsMode": "None", "AuthMode": "None" }
                }
              }
            }
            """);
        return configPath;
    }

    private static (int ExitCode, string Output) RunCommand(params string[] args) => TuiTestRunner.Run(args);

    [Fact]
    public void ProviderTest_ReachableServer_SucceedsAndExitsZero()
    {
        using var server = new FakeSmtpServer();
        var configPath = WriteConfig(server.Port);

        var result = RunCommand("provider", "test", "--config", configPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("succeeded", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProviderTest_UnreachableServer_ReportsWarningButExitsZero()
    {
        // Grab a free port, then release it immediately so nothing is listening there.
        int freePort;
        using (var probe = new FakeSmtpServer())
        {
            freePort = probe.Port;
        }

        var configPath = WriteConfig(freePort);

        var result = RunCommand("provider", "test", "--config", configPath, "--timeout", "2");

        // This is the central assertion: a failed underlying provider check must never surface as
        // a non-zero exit code or a thrown exception - it is a warning only.
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Warning", result.Output);
    }

    [Fact]
    public void ProviderTest_MissingConfigFile_ExitsNonZero()
    {
        var result = RunCommand("provider", "test", "--config", Path.Combine(_root, "does-not-exist.json"));

        Assert.NotEqual(0, result.ExitCode);
    }
}
