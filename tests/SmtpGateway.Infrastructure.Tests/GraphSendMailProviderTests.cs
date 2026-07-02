using System.Net;
using System.Net.Http.Headers;
using SmtpGateway.Core;
using Xunit;

namespace SmtpGateway.Infrastructure.Tests;

public sealed class GraphSendMailProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string SecretToken = "s3cr3t-access-token-do-not-leak";

    private static byte[] RawMime => "From: sender@example.com\r\nTo: rcpt1@example.com\r\nSubject: Test\r\n\r\nHello\r\n"u8.ToArray();

    private sealed class FakeTokenProvider(string token) : ITokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct) => Task.FromResult(token);
    }

    /// <summary>
    /// Test-only fake Graph HTTP endpoint: replays a scripted queue of responses (or throws), one
    /// per request, in call order.
    /// </summary>
    private sealed class ScriptedGraphHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        /// <summary>
        /// Request bodies captured eagerly (parallel to <see cref="Requests"/>) because the
        /// provider disposes each request's <see cref="HttpContent"/> once <c>SendAsync</c>
        /// returns, so reading it back from the stored <see cref="HttpRequestMessage"/> afterwards
        /// would throw <see cref="ObjectDisposedException"/>.
        /// </summary>
        public List<byte[]?> RequestBodies { get; } = [];

        public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responders.Enqueue(responder);

        public void EnqueueThrow(Exception exception) => _responders.Enqueue(_ => throw exception);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            RequestBodies.Add(request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken));
            if (_responders.Count == 0)
            {
                throw new InvalidOperationException("ScriptedGraphHandler received a request with no scripted response left.");
            }

            return _responders.Dequeue()(request);
        }
    }

    private static GraphSendMailProviderOptions OptionsFor(ITokenProvider tokenProvider) => new()
    {
        Mailbox = "gateway@tenant.onmicrosoft.com",
        TokenProvider = tokenProvider,
        GraphBaseUrl = "https://graph.example.test/v1.0",
    };

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string? jsonBody = null)
    {
        var response = new HttpResponseMessage(statusCode);
        if (jsonBody is not null)
        {
            response.Content = new StringContent(jsonBody);
        }

        return response;
    }

    [Fact]
    public async Task Submit_FullSuccessPath_ReturnsSuccessForAllRecipientsAndDoesNotThrow()
    {
        var handler = new ScriptedGraphHandler();
        handler.Enqueue(_ => CreateResponse(HttpStatusCode.Accepted));
        using var httpClient = new HttpClient(handler);
        var provider = new GraphSendMailProvider(OptionsFor(new FakeTokenProvider(SecretToken)), httpClient);
        var envelope = new Envelope("sender@example.com", ["rcpt1@example.com", "rcpt2@example.com"]);

        var results = await provider.Submit(envelope, RawMime, Ct);

        Assert.Equal(OutboundSubmitResult.Success, results["rcpt1@example.com"].Result);
        Assert.Equal(OutboundSubmitResult.Success, results["rcpt2@example.com"].Result);
        Assert.Null(results["rcpt1@example.com"].RetryAfter);

        Assert.Single(handler.Requests);
        var sendRequest = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, sendRequest.Method);
        Assert.Equal(
            "https://graph.example.test/v1.0/users/gateway%40tenant.onmicrosoft.com/sendMail",
            sendRequest.RequestUri!.ToString());
        Assert.Equal("text/plain", sendRequest.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("Bearer", sendRequest.Headers.Authorization!.Scheme);
        Assert.Equal(SecretToken, sendRequest.Headers.Authorization!.Parameter);

        var sendBody = handler.RequestBodies[0]!;
        Assert.Equal(Convert.ToBase64String(RawMime), System.Text.Encoding.ASCII.GetString(sendBody));
    }

    [Fact]
    public async Task Submit_TooManyRequestsWithRetryAfter_ReturnsRetryableFailureWithParsedTimeSpan()
    {
        var handler = new ScriptedGraphHandler();
        handler.Enqueue(_ =>
        {
            var response = CreateResponse((HttpStatusCode)429);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        });
        using var httpClient = new HttpClient(handler);
        var provider = new GraphSendMailProvider(OptionsFor(new FakeTokenProvider(SecretToken)), httpClient);
        var envelope = new Envelope("sender@example.com", ["rcpt1@example.com", "rcpt2@example.com"]);

        var results = await provider.Submit(envelope, RawMime, Ct);

        Assert.All(results.Values, outcome =>
        {
            Assert.Equal(OutboundSubmitResult.RetryableFailure, outcome.Result);
            Assert.Equal(TimeSpan.FromSeconds(120), outcome.RetryAfter);
        });
    }

    [Fact]
    public async Task Submit_TooManyRequestsWithoutRetryAfter_ReturnsRetryableFailureWithNullHint()
    {
        var handler = new ScriptedGraphHandler();
        handler.Enqueue(_ => CreateResponse((HttpStatusCode)429));
        using var httpClient = new HttpClient(handler);
        var provider = new GraphSendMailProvider(OptionsFor(new FakeTokenProvider(SecretToken)), httpClient);
        var envelope = new Envelope("sender@example.com", ["rcpt1@example.com"]);

        var results = await provider.Submit(envelope, RawMime, Ct);

        Assert.Equal(OutboundSubmitResult.RetryableFailure, results["rcpt1@example.com"].Result);
        Assert.Null(results["rcpt1@example.com"].RetryAfter);
    }

    [Fact]
    public async Task Submit_Forbidden_ReturnsPermanentFailureForAllRecipients()
    {
        var handler = new ScriptedGraphHandler();
        handler.Enqueue(_ => CreateResponse(HttpStatusCode.Forbidden));
        using var httpClient = new HttpClient(handler);
        var provider = new GraphSendMailProvider(OptionsFor(new FakeTokenProvider(SecretToken)), httpClient);
        var envelope = new Envelope("sender@example.com", ["rcpt1@example.com", "rcpt2@example.com"]);

        var results = await provider.Submit(envelope, RawMime, Ct);

        Assert.All(results.Values, outcome => Assert.Equal(OutboundSubmitResult.PermanentFailure, outcome.Result));
    }

    [Fact]
    public async Task Submit_Unauthorized_ReturnsRetryableFailureForAllRecipients()
    {
        var handler = new ScriptedGraphHandler();
        handler.Enqueue(_ => CreateResponse(HttpStatusCode.Unauthorized));
        using var httpClient = new HttpClient(handler);
        var provider = new GraphSendMailProvider(OptionsFor(new FakeTokenProvider(SecretToken)), httpClient);
        var envelope = new Envelope("sender@example.com", ["rcpt1@example.com", "rcpt2@example.com"]);

        var results = await provider.Submit(envelope, RawMime, Ct);

        Assert.All(results.Values, outcome => Assert.Equal(OutboundSubmitResult.RetryableFailure, outcome.Result));
    }

    [Fact]
    public async Task Submit_HttpRequestExceptionFromHandler_ReturnsRetryableFailureForAllRecipients()
    {
        var handler = new ScriptedGraphHandler();
        handler.EnqueueThrow(new HttpRequestException("simulated transport failure"));
        using var httpClient = new HttpClient(handler);
        var provider = new GraphSendMailProvider(OptionsFor(new FakeTokenProvider(SecretToken)), httpClient);
        var envelope = new Envelope("sender@example.com", ["rcpt1@example.com", "rcpt2@example.com"]);

        var results = await provider.Submit(envelope, RawMime, Ct);

        Assert.All(results.Values, outcome => Assert.Equal(OutboundSubmitResult.RetryableFailure, outcome.Result));
    }

    [Fact]
    public async Task TestMailboxAccessAsync_MailboxReadable_ReturnsSuccess()
    {
        var handler = new ScriptedGraphHandler();
        handler.Enqueue(_ => CreateResponse(HttpStatusCode.OK, """{"id":"user-123","mail":"gateway@tenant.onmicrosoft.com"}"""));
        using var httpClient = new HttpClient(handler);
        var provider = new GraphSendMailProvider(OptionsFor(new FakeTokenProvider(SecretToken)), httpClient);

        var result = await provider.TestMailboxAccessAsync(Ct);

        Assert.True(result.Success);
        Assert.Null(result.ErrorMessage);
        Assert.Single(handler.Requests);
        var request = handler.Requests[0];
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            "https://graph.example.test/v1.0/users/gateway%40tenant.onmicrosoft.com",
            request.RequestUri!.ToString());
        Assert.Equal(SecretToken, request.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task TestMailboxAccessAsync_MailboxNotFound_ReturnsFailureWithoutToken()
    {
        var handler = new ScriptedGraphHandler();
        handler.Enqueue(_ => CreateResponse(HttpStatusCode.NotFound));
        using var httpClient = new HttpClient(handler);
        var provider = new GraphSendMailProvider(OptionsFor(new FakeTokenProvider(SecretToken)), httpClient);

        var result = await provider.TestMailboxAccessAsync(Ct);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.DoesNotContain(SecretToken, result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestMailboxAccessAsync_HttpRequestExceptionFromHandler_ReturnsFailureWithoutThrowing()
    {
        var handler = new ScriptedGraphHandler();
        handler.EnqueueThrow(new HttpRequestException($"connection reset while sending request with token {SecretToken}"));
        using var httpClient = new HttpClient(handler);
        var provider = new GraphSendMailProvider(OptionsFor(new FakeTokenProvider(SecretToken)), httpClient);

        var result = await provider.TestMailboxAccessAsync(Ct);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.DoesNotContain(SecretToken, result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestMailboxAccessAsync_TimedOutToken_ReturnsFailureWithoutThrowing()
    {
        // Simulates the caller's timeout-linked token firing mid-request (the handler itself
        // never checks the token, so cancellation is modeled as the underlying send throwing
        // OperationCanceledException, exactly as HttpClient does when its own token expires).
        var handler = new ScriptedGraphHandler();
        handler.EnqueueThrow(new OperationCanceledException("simulated timeout"));
        using var httpClient = new HttpClient(handler);
        var provider = new GraphSendMailProvider(OptionsFor(new FakeTokenProvider(SecretToken)), httpClient);

        var result = await provider.TestMailboxAccessAsync(Ct);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task Submit_NetworkFailure_NeverExposesTokenAndDoesNotThrow()
    {
        // Worst-case adversarial handler: even if a lower-level exception message happened to
        // echo request details, Submit must swallow it into a RetryableFailure outcome rather
        // than letting it (and thus a possible token substring) propagate to the caller.
        var handler = new ScriptedGraphHandler();
        handler.EnqueueThrow(new HttpRequestException($"connection reset while sending request with token {SecretToken}"));
        using var httpClient = new HttpClient(handler);
        var provider = new GraphSendMailProvider(OptionsFor(new FakeTokenProvider(SecretToken)), httpClient);
        var envelope = new Envelope("sender@example.com", ["rcpt1@example.com"]);

        Exception? thrown = null;
        IReadOnlyDictionary<string, SubmitOutcome>? results = null;
        try
        {
            results = await provider.Submit(envelope, RawMime, Ct);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        Assert.Null(thrown);
        Assert.NotNull(results);
        Assert.Equal(OutboundSubmitResult.RetryableFailure, results["rcpt1@example.com"].Result);
    }
}
