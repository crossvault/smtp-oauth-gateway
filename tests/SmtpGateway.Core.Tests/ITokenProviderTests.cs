using SmtpGateway.Core;
using Xunit;

namespace SmtpGateway.Core.Tests;

public class ITokenProviderTests
{
    private sealed class FakeTokenProvider : ITokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct) => Task.FromResult("fake-token");
    }

    [Fact]
    public async Task GetAccessTokenAsync_ReturnsToken()
    {
        ITokenProvider provider = new FakeTokenProvider();

        var token = await provider.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal("fake-token", token);
    }
}
