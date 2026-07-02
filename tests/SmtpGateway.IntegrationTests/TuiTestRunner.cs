using Spectre.Console;
using Spectre.Console.Cli.Testing;
using SmtpGateway.Admin.Tui;

namespace SmtpGateway.IntegrationTests;

/// <summary>
/// Shared helper for end-to-end command tests: runs the real <see cref="AdminTuiApp"/> command
/// tree via <see cref="CommandAppTester"/>. Commands write through the static
/// <see cref="AnsiConsole"/> (there is no DI container in this CLI), so the global
/// <see cref="AnsiConsole.Console"/> is temporarily redirected to a fresh
/// <see cref="Spectre.Console.Testing.TestConsole"/> for the duration of the call. Because that
/// redirection is a mutable global, every caller must go through this one shared, locked entry
/// point rather than each test class redirecting it independently - otherwise tests in different
/// classes running in parallel (xunit's default) can stomp on each other's console.
/// </summary>
internal static class TuiTestRunner
{
    private static readonly object ConsoleLock = new();

    public static (int ExitCode, string Output) Run(params string[] args)
    {
        lock (ConsoleLock)
        {
            // A generous width avoids Spectre wrapping GUID-length cells (e.g. the queue item id
            // column) across multiple lines, which would otherwise break substring assertions.
            var testConsole = new Spectre.Console.Testing.TestConsole { Profile = { Width = 300 } };
            var originalConsole = AnsiConsole.Console;
            AnsiConsole.Console = testConsole;
            try
            {
                var tester = new CommandAppTester();
                tester.Configure(AdminTuiApp.Configure);
                var result = tester.Run(args);
                return (result.ExitCode, testConsole.Output);
            }
            finally
            {
                AnsiConsole.Console = originalConsole;
            }
        }
    }
}
