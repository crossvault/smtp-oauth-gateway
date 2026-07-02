using System.Text;
using SmtpGateway.Core;
using Xunit;

namespace SmtpGateway.Infrastructure.Tests;

public sealed class GenericSmtpProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] RawMime => "From: sender@example.com\r\nTo: rcpt1@example.com\r\nSubject: Test\r\n\r\nHello\r\n"u8.ToArray();

    private static GenericSmtpProviderOptions OptionsFor(int port) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        TlsMode = SmtpTlsMode.None,
        AuthMode = AuthMode.None,
    };

    private sealed class FakeTokenProvider(string token) : ITokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct) => Task.FromResult(token);
    }

    private static string BuildXoauth2InitialResponse(string username, string accessToken) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"user={username}\u0001auth=Bearer {accessToken}\u0001\u0001"));

    [Fact]
    public async Task Submit_AllRecipientsAccepted_ReturnsSuccessForEach()
    {
        using var server = new FakeSmtpServer();
        var provider = new GenericSmtpProvider(OptionsFor(server.Port));
        var envelope = new Envelope("sender@example.com", ["rcpt1@example.com", "rcpt2@example.com"]);

        var results = await provider.Submit(envelope, RawMime, Ct);

        Assert.Equal(OutboundSubmitResult.Success, results["rcpt1@example.com"].Result);
        Assert.Equal(OutboundSubmitResult.Success, results["rcpt2@example.com"].Result);
    }

    [Fact]
    public async Task Submit_OneRecipientRejectedWith550_ReturnsMixedResult()
    {
        var recipientResponses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bad@example.com"] = "550 Mailbox unavailable",
        };
        using var server = new FakeSmtpServer(recipientResponses);
        var provider = new GenericSmtpProvider(OptionsFor(server.Port));
        var envelope = new Envelope("sender@example.com", ["good@example.com", "bad@example.com"]);

        var results = await provider.Submit(envelope, RawMime, Ct);

        Assert.Equal(OutboundSubmitResult.Success, results["good@example.com"].Result);
        Assert.Equal(OutboundSubmitResult.PermanentFailure, results["bad@example.com"].Result);
    }

    [Fact]
    public async Task Submit_ConnectionDroppedDuringConnect_ReturnsRetryableFailureForAllRecipients()
    {
        using var server = new FakeSmtpServer(dropOnConnect: true);
        var provider = new GenericSmtpProvider(OptionsFor(server.Port));
        var envelope = new Envelope("sender@example.com", ["rcpt1@example.com", "rcpt2@example.com"]);

        var results = await provider.Submit(envelope, RawMime, Ct);

        Assert.Equal(OutboundSubmitResult.RetryableFailure, results["rcpt1@example.com"].Result);
        Assert.Equal(OutboundSubmitResult.RetryableFailure, results["rcpt2@example.com"].Result);
    }

    [Fact]
    public async Task Submit_UnparseableRecipientAmongValidOnes_PermanentlyFailsOnlyThatRecipientAndDeliversTheRest()
    {
        using var server = new FakeSmtpServer();
        var provider = new GenericSmtpProvider(OptionsFor(server.Port));
        // "weird stuff@example.com" is what SmtpServer stores for an accepted quoted local part
        // (`"weird stuff"@example.com`, quote-stripped); MimeKit cannot parse it. The parse used to
        // run outside Submit's try and throw out of the method, failing the whole item.
        var envelope = new Envelope("sender@example.com", ["good@example.com", "weird stuff@example.com"]);

        var results = await provider.Submit(envelope, RawMime, Ct);

        Assert.Equal(OutboundSubmitResult.Success, results["good@example.com"].Result);
        Assert.Equal(OutboundSubmitResult.PermanentFailure, results["weird stuff@example.com"].Result);
    }

    [Fact]
    public async Task Submit_AllRecipientsUnparseable_ReturnsPermanentFailureForEachWithoutConnecting()
    {
        // dropOnConnect would turn any real connection attempt into a RetryableFailure; asserting
        // PermanentFailure proves Submit short-circuits before connecting when nothing is sendable.
        using var server = new FakeSmtpServer(dropOnConnect: true);
        var provider = new GenericSmtpProvider(OptionsFor(server.Port));
        var envelope = new Envelope("sender@example.com", ["weird stuff@example.com"]);

        var results = await provider.Submit(envelope, RawMime, Ct);

        Assert.Equal(OutboundSubmitResult.PermanentFailure, results["weird stuff@example.com"].Result);
    }

    [Fact]
    public async Task Submit_NullReturnPathSenderStoredAsAtSign_ReturnsPermanentFailureForAllRecipients()
    {
        using var server = new FakeSmtpServer();
        var provider = new GenericSmtpProvider(OptionsFor(server.Port));
        // MAIL FROM:<> is delivered by SmtpServer as User=""/Host="", which SpoolingMessageStore
        // stores as the literal "@". MailboxAddress.Parse rejects it; that used to be classified as
        // a generic RetryableFailure and retried for the whole TTL before silent expiry.
        var envelope = new Envelope("@", ["rcpt1@example.com", "rcpt2@example.com"]);

        var results = await provider.Submit(envelope, RawMime, Ct);

        Assert.Equal(OutboundSubmitResult.PermanentFailure, results["rcpt1@example.com"].Result);
        Assert.Equal(OutboundSubmitResult.PermanentFailure, results["rcpt2@example.com"].Result);
    }

    [Fact]
    public void Constructor_M365OauthWithoutTokenProvider_Throws()
    {
        var options = new GenericSmtpProviderOptions
        {
            Host = "127.0.0.1",
            Port = 25,
            AuthMode = AuthMode.M365Oauth,
            Username = "user@example.com",
            TokenProvider = null,
        };

        Assert.Throws<ArgumentException>(() => new GenericSmtpProvider(options));
    }

    [Fact]
    public void Constructor_M365OauthWithoutUsername_Throws()
    {
        var options = new GenericSmtpProviderOptions
        {
            Host = "127.0.0.1",
            Port = 25,
            AuthMode = AuthMode.M365Oauth,
            Username = null,
            TokenProvider = new FakeTokenProvider("token"),
        };

        Assert.Throws<ArgumentException>(() => new GenericSmtpProvider(options));
    }

    [Fact]
    public async Task Submit_M365OauthAcceptedByServer_ReturnsSuccessForAllRecipients()
    {
        const string username = "user@example.com";
        const string accessToken = "fake-access-token";
        var initialResponse = BuildXoauth2InitialResponse(username, accessToken);

        using var server = new FakeSmtpServer(expectedXoauth2InitialResponse: initialResponse);
        var options = new GenericSmtpProviderOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            TlsMode = SmtpTlsMode.None,
            AuthMode = AuthMode.M365Oauth,
            Username = username,
            TokenProvider = new FakeTokenProvider(accessToken),
        };
        var provider = new GenericSmtpProvider(options);
        var envelope = new Envelope("sender@example.com", ["rcpt1@example.com", "rcpt2@example.com"]);

        var results = await provider.Submit(envelope, RawMime, Ct);

        Assert.Equal(OutboundSubmitResult.Success, results["rcpt1@example.com"].Result);
        Assert.Equal(OutboundSubmitResult.Success, results["rcpt2@example.com"].Result);
    }

    [Fact]
    public async Task Submit_M365OauthRejectedByServer_ReturnsRetryableFailureForAllRecipients()
    {
        const string username = "user@example.com";
        const string wrongAccessToken = "wrong-access-token";
        var expectedInitialResponse = BuildXoauth2InitialResponse(username, "correct-access-token");

        using var server = new FakeSmtpServer(expectedXoauth2InitialResponse: expectedInitialResponse);
        var options = new GenericSmtpProviderOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            TlsMode = SmtpTlsMode.None,
            AuthMode = AuthMode.M365Oauth,
            Username = username,
            TokenProvider = new FakeTokenProvider(wrongAccessToken),
        };
        var provider = new GenericSmtpProvider(options);
        var envelope = new Envelope("sender@example.com", ["rcpt1@example.com", "rcpt2@example.com"]);

        // GenericSmtpProvider.Submit swallows the underlying MailKit AuthenticationException
        // internally (per the established catch-all convention) and never lets it propagate, so
        // no exception message from this call can ever reach a caller/log in the first place.
        var results = await provider.Submit(envelope, RawMime, Ct);

        Assert.Equal(OutboundSubmitResult.RetryableFailure, results["rcpt1@example.com"].Result);
        Assert.Equal(OutboundSubmitResult.RetryableFailure, results["rcpt2@example.com"].Result);
    }

    [Fact]
    public async Task TestConnectionAsync_ConnectAuthNoOpAllSucceed_ReturnsSuccess()
    {
        using var server = new FakeSmtpServer();
        var provider = new GenericSmtpProvider(OptionsFor(server.Port));

        var result = await provider.TestConnectionAsync(Ct);

        Assert.True(result.Success);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task TestConnectionAsync_TimedOutToken_ReturnsFailureWithoutThrowing()
    {
        using var server = new FakeSmtpServer();
        var provider = new GenericSmtpProvider(OptionsFor(server.Port));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await provider.TestConnectionAsync(cts.Token);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task TestConnectionAsync_ConnectionDropped_ReturnsFailureWithoutThrowing()
    {
        using var server = new FakeSmtpServer(dropOnConnect: true);
        var provider = new GenericSmtpProvider(OptionsFor(server.Port));

        var result = await provider.TestConnectionAsync(Ct);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task TestConnectionAsync_M365OauthRejectedByServer_ReturnsFailureWithoutTokenInMessage()
    {
        const string username = "user@example.com";
        const string wrongAccessToken = "wrong-access-token";
        var expectedInitialResponse = BuildXoauth2InitialResponse(username, "correct-access-token");

        using var server = new FakeSmtpServer(expectedXoauth2InitialResponse: expectedInitialResponse);
        var options = new GenericSmtpProviderOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            TlsMode = SmtpTlsMode.None,
            AuthMode = AuthMode.M365Oauth,
            Username = username,
            TokenProvider = new FakeTokenProvider(wrongAccessToken),
        };
        var provider = new GenericSmtpProvider(options);

        var result = await provider.TestConnectionAsync(Ct);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.DoesNotContain(wrongAccessToken, result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task M365OauthRejectedByServer_UnderlyingMailKitExceptionDoesNotContainToken()
    {
        const string username = "user@example.com";
        const string wrongAccessToken = "wrong-access-token";
        var expectedInitialResponse = BuildXoauth2InitialResponse(username, "correct-access-token");

        using var server = new FakeSmtpServer(expectedXoauth2InitialResponse: expectedInitialResponse);
        using var client = new MailKit.Net.Smtp.SmtpClient();
        await client.ConnectAsync("127.0.0.1", server.Port, MailKit.Security.SecureSocketOptions.None, Ct);

        var mechanism = new MailKit.Security.SaslMechanismOAuth2(username, wrongAccessToken);
        var ex = await Assert.ThrowsAsync<MailKit.Security.AuthenticationException>(
            () => client.AuthenticateAsync(mechanism, Ct));

        Assert.DoesNotContain(wrongAccessToken, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(wrongAccessToken, ex.ToString(), StringComparison.Ordinal);
    }
}
