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

    public static (int ExitCode, string Output) Run(params string[] args) => Run(null, args);

    /// <summary>
    /// Runs the command tree, optionally scripting interactive prompt input first (for prompt-driven
    /// commands like the 'setup' wizard). When <paramref name="scriptInput"/> is non-null the test
    /// console is marked interactive and the callback pushes keystrokes/text onto its input queue in
    /// the exact order the command's prompts will consume them.
    /// </summary>
    public static (int ExitCode, string Output) Run(
        Action<Spectre.Console.Testing.TestConsoleInput>? scriptInput,
        params string[] args)
    {
        lock (ConsoleLock)
        {
            // A generous width avoids Spectre wrapping GUID-length cells (e.g. the queue item id
            // column) across multiple lines, which would otherwise break substring assertions.
            var testConsole = new Spectre.Console.Testing.TestConsole { Profile = { Width = 300 } };
            if (scriptInput is not null)
            {
                // SelectionPrompt/TextPrompt refuse to run on a non-interactive terminal.
                testConsole.Profile.Capabilities.Interactive = true;
                scriptInput(testConsole.Input);
            }

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

    /// <summary>
    /// Runs the no-args interactive shell (<see cref="InteractiveShell.RunAsync(string?, CancellationToken)"/>)
    /// against the same redirected, scripted <see cref="Spectre.Console.Testing.TestConsole"/>. The
    /// shell bypasses <c>CommandApp</c>, so this is a parallel entry point to <see cref="Run"/>; the
    /// console is always marked interactive because the shell is built entirely from
    /// SelectionPrompts, which refuse to run on a non-interactive terminal.
    /// </summary>
    public static (int ExitCode, string Output) RunShell(
        Action<Spectre.Console.Testing.TestConsoleInput> scriptInput,
        string? configPath)
    {
        ArgumentNullException.ThrowIfNull(scriptInput);

        lock (ConsoleLock)
        {
            var testConsole = new Spectre.Console.Testing.TestConsole { Profile = { Width = 300 } };
            testConsole.Profile.Capabilities.Interactive = true;
            scriptInput(testConsole.Input);

            var originalConsole = AnsiConsole.Console;
            AnsiConsole.Console = testConsole;
            try
            {
                var exitCode = InteractiveShell.RunAsync(configPath).GetAwaiter().GetResult();
                return (exitCode, testConsole.Output);
            }
            finally
            {
                AnsiConsole.Console = originalConsole;
            }
        }
    }
}
