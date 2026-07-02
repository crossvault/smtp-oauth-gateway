using SmtpGateway.Core;
using Xunit;

namespace SmtpGateway.E2ETests;

/// <summary>
/// The crown-piece live test: legacy client -> local loopback gateway (spool + SQLite queue) ->
/// real Microsoft 365 via the M365 SMTP OAuth provider. A message is submitted into the gateway
/// exactly like the integration tests do, then <see cref="Infrastructure.OutboundDeliveryWorker.ProcessNextAsync"/>
/// delivers it against the real tenant and the queue item must reach
/// <see cref="QueueItemStatus.Sent"/>. Skips cleanly when <c>.env</c> is absent. Shared pipeline
/// plumbing lives in <see cref="LivePipelineHarness"/>.
/// </summary>
public sealed class FullPipelineLiveTests
{
    [Fact]
    public async Task LegacyClient_ThroughGateway_DeliversToM365_ItemReachesSent()
    {
        var creds = E2ECredentials.Shared;
        Assert.SkipUnless(creds.Available, "Live O365 E2E credentials (.env) not present; skipping.");

        var ct = TestContext.Current.CancellationToken;

        await using var harness = await LivePipelineHarness.StartAsync(creds);

        // Legacy client submits into the local loopback gateway (no auth, plaintext) - exactly
        // how a real on-prem app would hand mail to the gateway.
        await harness.SendAsync(LiveTestMessage.Build(creds, "pipeline"), ct);

        var queued = Assert.Single(await harness.Repository.ListAsync(ct));
        Assert.Equal(QueueItemStatus.Queued, queued.Status);

        // Now deliver that queued item to the REAL tenant via the M365 SMTP OAuth provider.
        var reloaded = await harness.DeliverSingleAsync(ct);

        Assert.Equal(QueueItemStatus.Sent, reloaded.Status);
        Assert.All(reloaded.Recipients, recipient => Assert.Equal(RecipientStatus.Sent, recipient.Status));
    }
}
