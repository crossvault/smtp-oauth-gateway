using Spectre.Console.Cli;
using SmtpGateway.Admin.Tui.Commands;

namespace SmtpGateway.Admin.Tui;

/// <summary>
/// The full command tree for the admin CLI, factored out of Program.cs so it can be reused
/// verbatim by <c>CommandAppTester</c> in tests. Add new commands/branches here (e.g. the config
/// and provider-validation commands of a later phase) rather than in Program.cs directly.
/// </summary>
public static class AdminTuiApp
{
    public static void Configure(IConfigurator config)
    {
        config.SetApplicationName("smtpgw-admin");

        config.AddCommand<WizardCommand>("setup")
            .WithDescription("First-install interactive wizard: configure inbound, storage, and outbound provider, then write appsettings.json.");

        config.AddCommand<StatusCommand>("status")
            .WithDescription("Show the queue and provider status dashboard.");

        config.AddBranch("queue", queue =>
        {
            queue.SetDescription("Queue item operations.");

            queue.AddCommand<QueueListCommand>("list")
                .WithDescription("List queue items, optionally filtered by --status.");

            queue.AddCommand<QueueShowCommand>("show")
                .WithDescription("Show full detail for one queue item.");

            queue.AddCommand<QueueRetryCommand>("retry")
                .WithDescription("Retry a queue item: reset non-Sent recipients to Retryable.");

            queue.AddCommand<QueueDiscardCommand>("discard")
                .WithDescription("Discard a queue item: stop further delivery attempts.");

            queue.AddCommand<QueueExportCommand>("export")
                .WithDescription("Export a queue item's raw MIME to exports/<id>.eml.");
        });

        config.AddBranch("config", configBranch =>
        {
            configBranch.SetDescription("View and edit appsettings.json Gateway configuration.");

            configBranch.AddCommand<ConfigShowCommand>("show")
                .WithDescription("Show every Gateway setting as dotted-path -> value (secrets shown in cleartext).");

            configBranch.AddCommand<ConfigSetCommand>("set")
                .WithDescription("Set a single Gateway setting by dotted path, e.g. 'Smtp:MaxRecipients'.");

            configBranch.AddCommand<ConfigValidateCommand>("validate")
                .WithDescription("Validate appsettings.json against GatewayOptionsValidator and the outbound provider factory.");
        });

        config.AddBranch("provider", provider =>
        {
            provider.SetDescription("Outbound provider operations.");

            provider.AddCommand<ProviderTestCommand>("test")
                .WithDescription("Run an active connectivity/health check against the configured outbound provider (warning-only).");
        });
    }
}
