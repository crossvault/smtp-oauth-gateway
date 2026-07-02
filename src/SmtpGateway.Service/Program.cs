using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;
using Serilog;
using SmtpGateway.Core;
using SmtpGateway.Infrastructure;
using SmtpGateway.Service;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "SmtpGateway");

// A fatal exception in either hosted service must stop the host (and thus, together with the
// exit-code handling below, exit the process non-zero) - no degraded mode, no silent ignore.
builder.Services.Configure<HostOptions>(options =>
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);

// Config is bound once at startup (no hot reload by design) and validated fail-fast:
// ValidateDataAnnotations() checks GatewayOptions' own attributes, and the extra Validate() call
// reuses GatewayOptionsValidator for the recursive Smtp/OutboundProvider/bind-endpoint checks it
// already implements. ValidateOnStart() makes an invalid config throw during host startup.
builder.Services
    .AddOptions<GatewayOptions>()
    .Bind(builder.Configuration.GetSection("Gateway"))
    .ValidateDataAnnotations()
    .Validate(
        options =>
        {
            GatewayOptionsValidator.Validate(options);
            return true;
        },
        "Gateway configuration failed validation.")
    .ValidateOnStart();

builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<GatewayOptions>>().Value);
builder.Services.AddSingleton(sp => new FileSpool(sp.GetRequiredService<GatewayOptions>().SpoolDirectory));
builder.Services.AddSingleton(sp => new SqliteQueueRepository(sp.GetRequiredService<GatewayOptions>().QueueDatabasePath));
builder.Services.AddSingleton(sp => OutboundProviderFactory.Create(sp.GetRequiredService<GatewayOptions>().OutboundProvider));
builder.Services.AddSingleton(sp =>
{
    var gatewayOptions = sp.GetRequiredService<GatewayOptions>();
    // Null (the default/unconfigured) means unlimited - no limiter is constructed and the worker
    // never throttles, matching today's behavior exactly.
    var rateLimiter = gatewayOptions.OutboundRateLimitPerMinute is { } perMinute
        ? new SlidingWindowRateLimiter(perMinute, TimeSpan.FromMinutes(1))
        : null;

    return new OutboundDeliveryWorker(
        sp.GetRequiredService<SqliteQueueRepository>(),
        sp.GetRequiredService<FileSpool>(),
        sp.GetRequiredService<IOutboundProvider>(),
        gatewayOptions.SenderRewriteAddress,
        gatewayOptions.LeaseDuration,
        rateLimiter,
        utcNowProvider: null,
        logger: sp.GetRequiredService<ILogger<OutboundDeliveryWorker>>());
});

builder.Services.AddHostedService<InboundHostedService>();
builder.Services.AddHostedService<OutboundDeliveryHostedService>();

builder.Services.AddSerilog(
    (_, loggerConfiguration) => loggerConfiguration
        .MinimumLevel.Information()
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(builder.Environment.ContentRootPath, "logs", "smtpgateway-.log"),
            rollingInterval: RollingInterval.Day),
    writeToProviders: true);

// Windows EventLog carries ONLY service lifecycle events (start/stop) and Critical errors, never
// routine per-message delivery logs - see LifecycleLog and the AddFilter below. Registration is
// guarded: if the event source can't be created/accessed (e.g. non-elevated context), that must
// never prevent the SMTP/queue functionality from working, so we skip the provider and log a
// warning through Serilog once the host is built instead.
const string EventLogSourceName = "SmtpGateway";
var eventLogAvailable = false;
Exception? eventLogError = null;

if (OperatingSystem.IsWindows())
{
    try
    {
        if (!System.Diagnostics.EventLog.SourceExists(EventLogSourceName))
        {
            System.Diagnostics.EventLog.CreateEventSource(EventLogSourceName, "Application");
        }

        eventLogAvailable = true;
    }
    catch (Exception ex)
    {
        eventLogError = ex;
    }
}

if (eventLogAvailable)
{
    builder.Logging.AddEventLog(settings =>
    {
        settings.SourceName = EventLogSourceName;
        settings.LogName = "Application";
    });

    builder.Logging.AddFilter<EventLogLoggerProvider>((category, level) =>
        level == LogLevel.Critical ||
        (category == LifecycleLog.CategoryName && level >= LogLevel.Information));
}

var app = builder.Build();

// Resolved once, before RunAsync, and reused in the catch below: RunAsync disposes the host (and
// its DI container) before rethrowing a startup failure, so resolving services from app.Services
// *after* that would itself throw ObjectDisposedException. This logger instance was already
// bound to its providers at creation time, so it keeps working after the container is disposed.
var lifecycleLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(LifecycleLog.CategoryName);

if (!eventLogAvailable && eventLogError is not null)
{
    lifecycleLogger.LogWarning(
        eventLogError,
        "Could not register the Windows Event Log source '{SourceName}'; lifecycle/critical logs will only reach console/file.",
        EventLogSourceName);
}

try
{
    await app.RunAsync();
}
catch (Exception ex)
{
    // Invalid configuration (ValidateOnStart) or any other fatal startup/execution error: log and
    // exit non-zero - no degraded mode, no automatic repair.
    lifecycleLogger.LogCritical(ex, "Fatal error during host startup or execution; exiting.");
    Environment.ExitCode = 1;
}
