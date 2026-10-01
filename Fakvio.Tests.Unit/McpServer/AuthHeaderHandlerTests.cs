using System.Net.Http.Headers;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Configuration;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>
/// Tests for AuthHeaderHandler — the DelegatingHandler that authenticates
/// outbound API calls from the MCP server.
///
/// The point of these tests is the security property behind issue #239: the
/// bearer token must belong to the request being sent, not to the process. If
/// the token were stored on HttpClient.DefaultRequestHeaders (as it used to be),
/// every request would carry whatever credential was captured at startup — under
/// HTTP hosting that is one user's token on another user's call.
/// </summary>
public class AuthHeaderHandlerTests
{
    /// <summary>
    /// The core regression test: two requests through the SAME HttpClient while
    /// the provider reports two different tokens. Each request must carry its own.
    ///
    /// A startup-captured token cannot pass this test — client defaults are shared,
    /// so both requests would show the same value.
    /// </summary>
    [Fact]
    public async Task SendAsync_TwoRequestsWithDifferentTokens_EachCarriesItsOwn()
    {
        // Arrange: provider hands out "token-user-a" first, then "token-user-b"
        var provider = Substitute.For<IApiTokenProvider>();
        provider.GetToken().Returns("token-user-a", "token-user-b");

        var recorder = new RecordingHandler();
        using var client = new HttpClient(new AuthHeaderHandler(provider, new Fakvio.McpServer.Configuration.McpServerSettings()) { InnerHandler = recorder });

        // Act: two calls on one shared client, the way two tool calls would arrive
        await client.GetAsync("https://test-api.fakvio.cz/api/invoice/1");
        await client.GetAsync("https://test-api.fakvio.cz/api/invoice/2");

        // Assert: no bleed between the two requests
        recorder.SeenTokens.Count.ShouldBe(2);
        recorder.SeenTokens[0].ShouldBe("token-user-a");
        recorder.SeenTokens[1].ShouldBe("token-user-b");
    }

    [Fact]
    public async Task SendAsync_SetsBearerScheme()
    {
        var provider = Substitute.For<IApiTokenProvider>();
        provider.GetToken().Returns("jwt-token");

        var recorder = new RecordingHandler();
        using var client = new HttpClient(new AuthHeaderHandler(provider, new Fakvio.McpServer.Configuration.McpServerSettings()) { InnerHandler = recorder });

        await client.GetAsync("https://test-api.fakvio.cz/api/invoice/1");

        recorder.SeenSchemes.ShouldHaveSingleItem().ShouldBe("Bearer");
    }

    /// <summary>
    /// A missing token must not throw — the request goes out unauthenticated and
    /// the API answers 401, which the tool layer already knows how to report.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SendAsync_WithoutToken_SendsNoAuthorizationHeader(string? token)
    {
        var provider = Substitute.For<IApiTokenProvider>();
        provider.GetToken().Returns(token);

        var recorder = new RecordingHandler();
        using var client = new HttpClient(new AuthHeaderHandler(provider, new Fakvio.McpServer.Configuration.McpServerSettings()) { InnerHandler = recorder });

        await client.GetAsync("https://test-api.fakvio.cz/api/invoice/1");

        recorder.SeenTokens.ShouldHaveSingleItem().ShouldBeNull();
    }

    /// <summary>
    /// A request that already carries an Authorization header must come out the
    /// other side with that header REPLACED — cleared when the provider has no
    /// token, overwritten when it has one. The handler is the single authority
    /// on outbound credentials; anything the caller put there is not trusted.
    ///
    /// Unreachable through today's stdio path (FakvioApiClient never builds a
    /// request by hand), but the HTTP transport forwards the caller's own
    /// headers, so this is the semantics that path will depend on.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("token-user-b", "token-user-b")]
    public async Task SendAsync_PreExistingAuthorizationHeader_IsReplacedByTheProvidersAnswer(
        string? providerToken, string? expected)
    {
        var provider = Substitute.For<IApiTokenProvider>();
        provider.GetToken().Returns(providerToken);

        var recorder = new RecordingHandler();
        using var client = new HttpClient(new AuthHeaderHandler(provider, new Fakvio.McpServer.Configuration.McpServerSettings()) { InnerHandler = recorder });

        // GetAsync cannot pre-set headers, so the request is built explicitly
        var request = new HttpRequestMessage(HttpMethod.Get, "https://test-api.fakvio.cz/api/invoice/1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token-someone-else");

        await client.SendAsync(request);

        recorder.SeenTokens.ShouldHaveSingleItem().ShouldBe(expected);
    }

    /// <summary>
    /// stdio mode keeps its existing behaviour: the token read from
    /// FAKVIO_API_TOKEN at startup is what goes on the wire.
    /// </summary>
    [Fact]
    public async Task EnvironmentProvider_SuppliesTheStartupToken()
    {
        var settings = new McpServerSettings { ApiToken = "env-token" };
        var recorder = new RecordingHandler();
        using var client = new HttpClient(
            new AuthHeaderHandler(new EnvironmentApiTokenProvider(settings), settings) { InnerHandler = recorder });

        await client.GetAsync("https://test-api.fakvio.cz/api/invoice/1");

        recorder.SeenTokens.ShouldHaveSingleItem().ShouldBe("env-token");
    }

    /// <summary>
    /// Inner handler that records the Authorization header of every request it
    /// sees and short-circuits with 200 OK — no network involved.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string?> SeenTokens { get; } = new();
        public List<string?> SeenSchemes { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SeenTokens.Add(request.Headers.Authorization?.Parameter);
            SeenSchemes.Add(request.Headers.Authorization?.Scheme);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
