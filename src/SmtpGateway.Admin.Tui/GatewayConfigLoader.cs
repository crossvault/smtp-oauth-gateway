using Microsoft.Extensions.Configuration;
using SmtpGateway.Infrastructure;

namespace SmtpGateway.Admin.Tui;

/// <summary>
/// Loads the same appsettings.json-bound <see cref="GatewayOptions"/> the Service binds at
/// startup, so every TUI command reads queue database path, spool directory, and outbound
/// provider settings from the exact same source of truth - no parallel config model.
/// No hot reload: a fresh <see cref="GatewayOptions"/> is loaded once per command invocation.
/// </summary>
public static class GatewayConfigLoader
{
    public const string DefaultConfigFileName = "appsettings.json";

    /// <summary>
    /// Loads <see cref="GatewayOptions"/> from the "Gateway" section of <paramref name="configPath"/>
    /// (or <see cref="DefaultConfigFileName"/> in the current directory if null/empty).
    /// </summary>
    /// <exception cref="FileNotFoundException">The config file does not exist.</exception>
    /// <exception cref="InvalidOperationException">The file has no "Gateway" section.</exception>
    public static GatewayOptions Load(string? configPath)
    {
        var path = ResolvePath(configPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Configuration file '{path}' was not found.", path);
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false, reloadOnChange: false)
            .Build();

        return configuration.GetSection("Gateway").Get<GatewayOptions>()
            ?? throw new InvalidOperationException($"No 'Gateway' configuration section found in '{path}'.");
    }

    /// <summary>
    /// Resolves the effective config file path for a command's (possibly null/blank) '--config'
    /// option value, defaulting to <see cref="DefaultConfigFileName"/>. Shared by every command
    /// that needs the raw file path itself (not just the bound <see cref="GatewayOptions"/>), e.g.
    /// the 'config show'/'config set' commands that edit the raw JSON directly.
    /// </summary>
    public static string ResolvePath(string? configPath) =>
        string.IsNullOrWhiteSpace(configPath) ? DefaultConfigFileName : configPath;
}
