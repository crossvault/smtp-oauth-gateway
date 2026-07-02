using Microsoft.Extensions.Configuration;
using SmtpGateway.Infrastructure;
using Xunit;

namespace SmtpGateway.Infrastructure.Tests;

/// <summary>
/// Regression coverage for a real Microsoft.Extensions.Configuration.Binder pitfall: binding a
/// configuration array onto a property that already has a non-empty default value APPENDS to the
/// default instead of replacing it (see https://github.com/dotnet/runtime/issues/36117). If
/// <see cref="SmtpInboundOptions.BindEndpoints"/> had a non-empty compile-time default, a
/// configured "Smtp:BindEndpoints" list would end up listening on both the default and the
/// configured endpoints - not just the configured ones.
/// </summary>
public sealed class SmtpInboundOptionsBindingTests
{
    [Fact]
    public void Bind_ConfiguredBindEndpoints_ReplacesTheDefault_NotAppendsToIt()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
            [
                new("Smtp:BindEndpoints:0", "127.0.0.1:2527"),
            ])
            .Build();

        var options = new SmtpInboundOptions();
        configuration.GetSection("Smtp").Bind(options);

        Assert.Equal(["127.0.0.1:2527"], options.BindEndpoints);
    }
}
