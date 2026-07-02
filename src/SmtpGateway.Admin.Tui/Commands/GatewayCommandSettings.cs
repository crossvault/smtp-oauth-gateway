using System.ComponentModel;
using Spectre.Console.Cli;
using SmtpGateway.Core;

namespace SmtpGateway.Admin.Tui.Commands;

/// <summary>
/// Base settings shared by every command: which appsettings.json to read the Gateway
/// configuration from. Extend this for any new command so config-file discovery stays
/// consistent across the whole CLI.
/// </summary>
public class GatewayCommandSettings : CommandSettings
{
    [CommandOption("--config <PATH>")]
    [Description("Path to the appsettings.json file to read Gateway configuration from. Defaults to 'appsettings.json' in the current directory.")]
    public string? ConfigPath { get; set; }
}

/// <summary>Settings for commands that filter/list queue items.</summary>
public sealed class QueueListSettings : GatewayCommandSettings
{
    [CommandOption("--status <STATUS>")]
    [Description("Filter by queue item status (Queued, Leased, Sending, PartiallySent, Sent, RetryScheduled, Poison, Expired, Discarded).")]
    public QueueItemStatus? Status { get; set; }
}

/// <summary>Settings for commands that operate on a single queue item identified by id.</summary>
public sealed class QueueItemIdSettings : GatewayCommandSettings
{
    [CommandArgument(0, "<ID>")]
    [Description("The queue item id (GUID).")]
    public string Id { get; set; } = string.Empty;
}

/// <summary>Settings for 'config set &lt;path&gt; &lt;value&gt;'.</summary>
public sealed class ConfigSetSettings : GatewayCommandSettings
{
    [CommandArgument(0, "<PATH>")]
    [Description("Dotted path (':'-delimited) of the Gateway setting to set, e.g. 'Smtp:MaxRecipients' or 'OutboundProvider:GenericSmtp:Password'.")]
    public string Path { get; set; } = string.Empty;

    [CommandArgument(1, "<VALUE>")]
    [Description("The new value.")]
    public string Value { get; set; } = string.Empty;
}

/// <summary>Settings for 'provider test [--timeout &lt;seconds&gt;]'.</summary>
public sealed class ProviderTestSettings : GatewayCommandSettings
{
    [CommandOption("--timeout <SECONDS>")]
    [Description("Timeout in seconds for the connectivity check. Defaults to 10.")]
    public int? TimeoutSeconds { get; set; }
}
