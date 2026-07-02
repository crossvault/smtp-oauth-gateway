using SmtpGateway.Infrastructure;

namespace SmtpGateway.Admin.Tui;

/// <summary>
/// The shared 'config validate' core: re-reads appsettings.json, binds it to
/// <see cref="GatewayOptions"/>, runs <see cref="GatewayOptionsValidator"/> and the outbound
/// provider factory, and returns a single (Success, Message) result instead of writing to the
/// console - so both the <c>config validate</c> command and the interactive shell can present the
/// same outcome in their own visual style. Never throws for an ordinary invalid configuration.
/// </summary>
public static class ConfigValidation
{
    public readonly record struct Result(bool Success, string Message);

    public static Result Run(string? configPath)
    {
        GatewayOptions options;
        try
        {
            options = GatewayConfigLoader.Load(configPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
        {
            return new Result(false, $"Failed to load configuration: {ex.Message}");
        }

        try
        {
            GatewayOptionsValidator.Validate(options);
        }
        catch (Exception ex)
        {
            return new Result(false, $"Configuration is invalid: {ex.Message}");
        }

        try
        {
            OutboundProviderFactory.Create(options.OutboundProvider);
        }
        catch (Exception ex)
        {
            return new Result(false, $"Outbound provider configuration is invalid: {ex.Message}");
        }

        return new Result(true, "Configuration is valid.");
    }
}
