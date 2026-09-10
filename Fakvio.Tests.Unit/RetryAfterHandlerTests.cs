using System.Net;
using Fakvio.UI.Shared.Services;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for the client-side 503 retry.
///
/// The behaviour under test is narrow on purpose: only a 503 that carries Retry-After is
/// retried. A 503 without it is a real outage and has to reach the user; a request whose
/// body is a stream cannot be sent twice and must not be retried at all.
/// </summary>
public class RetryAfterHandlerTests
{
    /// <summary>
    /// Stands in for the network. Returns the queued responses in order and records how
    /// many times it was called, which is the only thing these tests need to assert.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public StubHandler(params HttpResponseMessage[] responses)
            => _responses = new Queue<HttpResponseMessage>(responses);

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            // Repeat the last response once the queue runs dry, so a test that expects
            // "keeps failing" does not have to enumerate every attempt.
            return Task.FromResult(_responses.Count > 1 ? _responses.Dequeue() : _responses.Peek());
        }
    }

    /// <summary>Builds a 503 whose Retry-After asks for the shortest possible wait.</summary>
    private static HttpResponseMessage Unavailable(bool withRetryAfter = true)
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        if (withRetryAfter)
        {
            // One second, not the production five: the test really waits this long.
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromSeconds(1));
        }
        return response;
    }

    private static HttpClient CreateClient(StubHandler stub)
        => new(new RetryAfterHandler { InnerHandler = stub });

    [Fact]
    public async Task Unavailable_WithRetryAfter_RetriesAndReturnsTheSuccess()
    {
        var stub = new StubHandler(Unavailable(), new HttpResponseMessage(HttpStatusCode.OK));

        var response = await CreateClient(stub).GetAsync("http://localhost/api/dashboard");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        stub.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task Unavailable_WithoutRetryAfter_IsNotRetried()
    {
        // No Retry-After means "the service is down", not "come back in a second".
        var stub = new StubHandler(Unavailable(withRetryAfter: false));

        var response = await CreateClient(stub).GetAsync("http://localhost/api/dashboard");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        stub.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task Unavailable_Persistently_GivesUpAfterTheAttemptCap()
    {
        // Three requests total: the first plus two retries. Without the cap a permanently
        // unavailable backend would hang the UI instead of showing an error.
        var stub = new StubHandler(Unavailable());

        var response = await CreateClient(stub).GetAsync("http://localhost/api/dashboard");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        stub.CallCount.ShouldBe(3);
    }

    [Fact]
    public async Task Ok_IsNotRetried()
    {
        var stub = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK));

        var response = await CreateClient(stub).GetAsync("http://localhost/api/dashboard");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        stub.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task StreamBody_IsNotRetried()
    {
        // A stream has already been consumed by the first attempt — resending would put an
        // empty body on the wire, which is worse than surfacing the 503.
        var stub = new StubHandler(Unavailable());
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/api/file")
        {
            Content = new StreamContent(new MemoryStream([1, 2, 3]))
        };

        var response = await CreateClient(stub).SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        stub.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task BufferedBody_IsRetried()
    {
        // JSON/form bodies can be read twice, and the startup gate answers before any
        // handler runs, so re-sending a POST cannot double-apply anything.
        var stub = new StubHandler(Unavailable(), new HttpResponseMessage(HttpStatusCode.OK));
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/api/auth/login")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        };

        var response = await CreateClient(stub).SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        stub.CallCount.ShouldBe(2);
    }
}
