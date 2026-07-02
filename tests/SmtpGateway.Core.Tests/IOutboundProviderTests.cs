using SmtpGateway.Core;
using Xunit;

namespace SmtpGateway.Core.Tests;

public class IOutboundProviderTests
{
    private sealed class FakeProvider : IOutboundProvider
    {
        public Task<IReadOnlyDictionary<string, SubmitOutcome>> Submit(Envelope envelope, byte[] rawMime, CancellationToken ct)
        {
            IReadOnlyDictionary<string, SubmitOutcome> result = envelope.Recipients
                .ToDictionary(r => r, _ => new SubmitOutcome(OutboundSubmitResult.Success));
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task Submit_ReturnsPerRecipientResult()
    {
        IOutboundProvider provider = new FakeProvider();
        var envelope = new Envelope("sender@example.com", ["a@example.com", "b@example.com"]);

        var result = await provider.Submit(envelope, [1, 2, 3], CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(OutboundSubmitResult.Success, result["a@example.com"].Result);
        Assert.Equal(OutboundSubmitResult.Success, result["b@example.com"].Result);
    }
}
