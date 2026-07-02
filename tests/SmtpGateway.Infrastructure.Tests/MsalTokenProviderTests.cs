using SmtpGateway.Infrastructure;
using Xunit;

namespace SmtpGateway.Infrastructure.Tests;

public sealed class MsalTokenProviderTests
{
    private sealed class FakeTokenAcquirer : ITokenAcquirer
    {
        private readonly Func<AcquiredToken> _next;
        private readonly TimeSpan _delay;

        public int CallCount;

        public FakeTokenAcquirer(Func<AcquiredToken> next, TimeSpan delay = default)
        {
            _next = next;
            _delay = delay;
        }

        public async Task<AcquiredToken> AcquireAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref CallCount);

            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, ct).ConfigureAwait(false);
            }

            return _next();
        }
    }

    private sealed class ThrowingTokenAcquirer(string message) : ITokenAcquirer
    {
        public Task<AcquiredToken> AcquireAsync(CancellationToken ct) =>
            throw new InvalidOperationException(message);
    }

    [Fact]
    public async Task GetAccessTokenAsync_FirstCall_AcquiresAndCachesToken()
    {
        var acquirer = new FakeTokenAcquirer(() => new AcquiredToken("token-1", DateTimeOffset.UtcNow.AddHours(1)));
        var provider = new MsalTokenProvider(acquirer);

        var token = await provider.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal("token-1", token);
        Assert.Equal(1, acquirer.CallCount);
    }

    [Fact]
    public async Task GetAccessTokenAsync_SecondCallBeforeExpiry_ReusesCachedToken()
    {
        var acquirer = new FakeTokenAcquirer(() => new AcquiredToken("token-1", DateTimeOffset.UtcNow.AddHours(1)));
        var provider = new MsalTokenProvider(acquirer);

        var first = await provider.GetAccessTokenAsync(CancellationToken.None);
        var second = await provider.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(1, acquirer.CallCount);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WithinRefreshMarginOfExpiry_AcquiresFreshToken()
    {
        var callNumber = 0;
        var acquirer = new FakeTokenAcquirer(() =>
        {
            callNumber++;
            // First token is already within the 5-minute refresh margin; the second call must
            // trigger a fresh acquisition rather than reusing it.
            var expiresOn = callNumber == 1
                ? DateTimeOffset.UtcNow.AddMinutes(2)
                : DateTimeOffset.UtcNow.AddHours(1);
            return new AcquiredToken($"token-{callNumber}", expiresOn);
        });
        var provider = new MsalTokenProvider(acquirer);

        var first = await provider.GetAccessTokenAsync(CancellationToken.None);
        var second = await provider.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal("token-1", first);
        Assert.Equal("token-2", second);
        Assert.Equal(2, acquirer.CallCount);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ConcurrentCallsWithNoCachedToken_AcquireExactlyOnce()
    {
        var acquirer = new FakeTokenAcquirer(
            () => new AcquiredToken("token-1", DateTimeOffset.UtcNow.AddHours(1)),
            delay: TimeSpan.FromMilliseconds(200));
        var provider = new MsalTokenProvider(acquirer);

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => provider.GetAccessTokenAsync(CancellationToken.None))
            .ToArray();
        var tokens = await Task.WhenAll(tasks);

        Assert.Equal(1, acquirer.CallCount);
        Assert.All(tokens, t => Assert.Equal("token-1", t));
    }

    [Fact]
    public async Task GetAccessTokenAsync_JoinerToken_NotCancelledByOtherJoinersCancellation()
    {
        var acquirer = new FakeTokenAcquirer(
            () => new AcquiredToken("token-1", DateTimeOffset.UtcNow.AddHours(1)),
            delay: TimeSpan.FromMilliseconds(200));
        var provider = new MsalTokenProvider(acquirer);

        using var ctsA = new CancellationTokenSource();
        var taskA = provider.GetAccessTokenAsync(ctsA.Token);
        var taskB = provider.GetAccessTokenAsync(CancellationToken.None);

        // Cancel only A's own token; B joined the same in-flight acquisition but must be
        // unaffected since B's own token was never cancelled.
        ctsA.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() => taskA);
        var tokenB = await taskB;

        Assert.Equal("token-1", tokenB);
        Assert.Equal(1, acquirer.CallCount);
    }

    [Fact]
    public async Task GetAccessTokenAsync_AcquirerThrows_ExceptionMessageDoesNotContainSecret()
    {
        const string secret = "super-secret-client-secret-value";
        var acquirer = new ThrowingTokenAcquirer(secret);
        var provider = new MsalTokenProvider(acquirer);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetAccessTokenAsync(CancellationToken.None));

        Assert.DoesNotContain(secret, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, ex.ToString(), StringComparison.Ordinal);
    }
}
