using System.Net;
using Fakvio.Functions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for <see cref="McpKeepAliveFunctions"/> — the timer that keeps the MCP HTTP host from
/// going cold (DEVGUIDE §9.1).
///
/// <para>
/// What is worth testing here is not that an HTTP call happens, but the three decisions around
/// it: stay silent when no host is configured, probe the right endpoint the right way, and never
/// let a failed ping escape. The last one matters most — this function runs every five minutes,
/// and an exception leaking out of it would turn the Function App red for something whose only
/// consequence is that one user might meet a slow first request.
/// </para>
///
/// Junior note: <c>HttpClient</c> is a concrete class, so the way to control what it "sees" is to
/// hand it a fake <c>HttpMessageHandler</c> — every request goes through that.
/// </summary>
public class McpKeepAliveFunctionsTests
{
    private const string HostUrl = "https://mcp-test.fakvio.cz";

    /// <summary>
    /// An environment with no MCP host must produce no traffic at all. Without this, every
    /// environment that has not deployed an MCP host would ping a nonexistent address and log a
    /// warning 288 times a day.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithNoConfiguredUrl_SendsNothing()
    {
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized);
        var sut = CreateSut(handler, configuredUrl: null);

        await sut.RunAsync(null!, CancellationToken.None);

        handler.Requests.ShouldBeEmpty();
    }

    /// <summary>
    /// The probe has to be the one request the MCP host can answer on its own: a POST to /mcp with
    /// no Authorization header, which <c>McpApiKeyMiddleware</c> rejects before it ever calls the
    /// API. A ping that carried a credential, or hit another path, would put load on the API and
    /// need a secret to run.
    /// </summary>
    [Fact]
    public async Task RunAsync_PostsToMcpEndpointWithoutCredential()
    {
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized);
        var sut = CreateSut(handler, HostUrl);

        await sut.RunAsync(null!, CancellationToken.None);

        var request = handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.ShouldBe(new Uri("https://mcp-test.fakvio.cz/mcp"));
        request.HadAuthorizationHeader.ShouldBeFalse();
    }

    /// <summary>
    /// A trailing slash on the configured address must not produce "//mcp". Cheap to get wrong in
    /// a settings field, and the resulting 404 would look like a broken host rather than a typo.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithTrailingSlashInUrl_StillTargetsMcpOnce()
    {
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized);
        var sut = CreateSut(handler, HostUrl + "/");

        await sut.RunAsync(null!, CancellationToken.None);

        handler.Requests.ShouldHaveSingleItem()
            .Uri.ShouldBe(new Uri("https://mcp-test.fakvio.cz/mcp"));
    }

    /// <summary>
    /// An unhealthy host is reported, not thrown. 500 is exactly what the cold-start race answers,
    /// so this is the case the function exists to observe — and the one where failing loudly would
    /// be worst, because the next tick fixes it.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenHostAnswersUnexpectedStatus_DoesNotThrow()
    {
        var handler = new RecordingHandler(HttpStatusCode.InternalServerError);
        var sut = CreateSut(handler, HostUrl);

        await Should.NotThrowAsync(() => sut.RunAsync(null!, CancellationToken.None));
    }

    /// <summary>
    /// Same contract when the host cannot be reached at all — a DNS failure, a refused connection,
    /// a timeout. The ping is best-effort by design.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenRequestFails_DoesNotThrow()
    {
        var handler = new RecordingHandler(new HttpRequestException("connection refused"));
        var sut = CreateSut(handler, HostUrl);

        await Should.NotThrowAsync(() => sut.RunAsync(null!, CancellationToken.None));
    }

    /// <summary>
    /// Cancellation is the one case that must propagate: it means the Functions host is shutting
    /// down, and swallowing it would report a clean run for work that never happened.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenCancelled_Propagates()
    {
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized);
        var sut = CreateSut(handler, HostUrl);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => sut.RunAsync(null!, cts.Token));
    }

    private static McpKeepAliveFunctions CreateSut(HttpMessageHandler handler, string? configuredUrl)
    {
        var settings = new Dictionary<string, string?>();
        if (configuredUrl is not null)
            settings["McpKeepAlive:Url"] = configuredUrl;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));

        return new McpKeepAliveFunctions(
            factory,
            configuration,
            NullLogger<McpKeepAliveFunctions>.Instance);
    }

    /// <summary>
    /// Records what was sent and replays a fixed outcome — either a status code or a throw.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode? _statusCode;
        private readonly Exception? _exception;

        public RecordingHandler(HttpStatusCode statusCode) => _statusCode = statusCode;

        public RecordingHandler(Exception exception) => _exception = exception;

        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization is not null));

            if (_exception is not null)
                return Task.FromException<HttpResponseMessage>(_exception);

            return Task.FromResult(new HttpResponseMessage(_statusCode!.Value));
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, bool HadAuthorizationHeader);
}
