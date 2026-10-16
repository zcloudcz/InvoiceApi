using System.Net;
using System.Text;
using Fakvio.Infrastructure.Authentication.OAuth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="OAuthClientResolver"/> (ADR 0001, docs/adr/0001-mcp-oauth21.md
/// §4.6, threat T7/T1). Network access is faked via a stub <see cref="HttpMessageHandler"/> so
/// these tests exercise the shape/allowlist/document validation and caching without touching a
/// real socket — <see cref="SsrfSafeConnectTests"/> covers the IP-blocking logic itself, and the
/// real <see cref="System.Net.Http.SocketsHttpHandler.ConnectCallback"/> wiring is exercised by
/// <c>McpHttpTransportTests</c>-style integration coverage (N5.6).
/// </summary>
public class OAuthClientResolverTests
{
    private const string ClientId = "https://claude.ai/oauth/claude-code-client-metadata";
    private const string ChatGptClientId = "https://chatgpt.com/oauth/client.json";

    private static readonly string ValidDocument = $$"""
        {
            "client_id": "{{ClientId}}",
            "client_name": "Claude Code",
            "redirect_uris": ["https://claude.ai/api/mcp/auth_callback", "http://127.0.0.1:12345/callback"]
        }
        """;

    private OAuthClientResolver CreateResolver(
        out StubHandler handler,
        string[]? trustedHosts = null,
        HttpResponseMessage? response = null)
    {
        handler = new StubHandler(response ?? OkJson(ValidDocument));

        var httpClient = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(OAuthClientResolver.HttpClientName).Returns(httpClient);

        var options = Options.Create(new McpOAuthOptions
        {
            TrustedClientHosts = trustedHosts ?? ["claude.ai", "chatgpt.com"]
        });

        return new OAuthClientResolver(factory, new MemoryCache(new MemoryCacheOptions()), options,
            NullLogger<OAuthClientResolver>.Instance);
    }

    private static HttpResponseMessage OkJson(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    // ─── client_id shape (ADR §4.6) ─────────────────────────────────────────

    [Theory]
    [InlineData("http://claude.ai/oauth/claude-code-client-metadata")] // not https
    [InlineData("https://claude.ai/oauth/foo#frag")]                    // fragment
    [InlineData("https://claude.ai/oauth/foo?x=1")]                     // query
    [InlineData("https://user@claude.ai/oauth/foo")]                    // userinfo
    [InlineData("https://claude.ai")]                                   // no path
    [InlineData("https://claude.ai/")]                                  // root path
    [InlineData("https://claude.ai:8443/oauth/foo")]                    // non-443 port
    [InlineData("not-a-url")]
    public async Task ResolveAsync_RejectsMalformedClientId_WithoutFetching(string clientId)
    {
        var resolver = CreateResolver(out var handler);

        var result = await resolver.ResolveAsync(clientId);

        result.ShouldBeNull();
        handler.CallCount.ShouldBe(0);
    }

    // ─── host allowlist (ADR §4.6, Q3) ──────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_RejectsHostOutsideAllowlist_WithoutFetching()
    {
        var resolver = CreateResolver(out var handler, trustedHosts: ["claude.ai"]);

        var result = await resolver.ResolveAsync("https://evil.example.com/client-metadata");

        result.ShouldBeNull();
        handler.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ResolveAsync_DoesNotAllowSubdomainOfTrustedHost()
    {
        // Exact match only — a compromised subdomain of a trusted host must not inherit trust.
        var resolver = CreateResolver(out var handler, trustedHosts: ["claude.ai"]);

        var result = await resolver.ResolveAsync("https://evil.claude.ai/client-metadata");

        result.ShouldBeNull();
        handler.CallCount.ShouldBe(0);
    }

    // ─── happy path + caching ────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_ReturnsDocument_ForValidCimd()
    {
        var resolver = CreateResolver(out _);

        var result = await resolver.ResolveAsync(ClientId);

        result.ShouldNotBeNull();
        result!.ClientId.ShouldBe(ClientId);
        result.ClientName.ShouldBe("Claude Code");
        result.RedirectUris.ShouldContain("https://claude.ai/api/mcp/auth_callback");
    }

    [Fact]
    public async Task ResolveAsync_CachesPositiveResult_DoesNotRefetch()
    {
        var resolver = CreateResolver(out var handler);

        await resolver.ResolveAsync(ClientId);
        await resolver.ResolveAsync(ClientId);

        handler.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task ResolveAsync_CachesNegativeResult_DoesNotRefetch()
    {
        var resolver = CreateResolver(out var handler, response: new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await resolver.ResolveAsync(ClientId);
        await resolver.ResolveAsync(ClientId);

        handler.CallCount.ShouldBe(1);
    }

    // ─── document validation ────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_RejectsDocument_WhenClientIdDoesNotMatch()
    {
        var body = ValidDocument.Replace(ClientId, "https://claude.ai/oauth/someone-else");
        var resolver = CreateResolver(out _, response: OkJson(body));

        (await resolver.ResolveAsync(ClientId)).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_RejectsDocument_WithNoRedirectUris()
    {
        var body = $$"""{"client_id": "{{ClientId}}", "client_name": "Claude Code", "redirect_uris": []}""";
        var resolver = CreateResolver(out _, response: OkJson(body));

        (await resolver.ResolveAsync(ClientId)).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_RejectsDocument_WithNonHttpsNonLoopbackRedirectUri()
    {
        var body = $$"""
            {"client_id": "{{ClientId}}", "client_name": "Claude Code",
             "redirect_uris": ["http://example.com/callback"]}
            """;
        var resolver = CreateResolver(out _, response: OkJson(body));

        (await resolver.ResolveAsync(ClientId)).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_AcceptsLoopbackHttpRedirectUri()
    {
        var body = $$"""
            {"client_id": "{{ClientId}}", "client_name": "Claude Code",
             "redirect_uris": ["http://127.0.0.1:54321/callback"]}
            """;
        var resolver = CreateResolver(out _, response: OkJson(body));

        var result = await resolver.ResolveAsync(ClientId);

        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task ResolveAsync_AcceptsChatGptCimdWhenPublicClientAuthIsSupported()
    {
        // ChatGPT's current CIMD prefers private_key_jwt but also supports "none".
        // Fakvio is a public client server, so the shared "none" method is the one to use.
        var body = $$"""
            {"client_id":"{{ChatGptClientId}}","client_name":"ChatGPT",
             "redirect_uris":["https://chatgpt.com/connector_platform_oauth_redirect"],
             "token_endpoint_auth_method":"private_key_jwt",
             "token_endpoint_auth_methods_supported":["none","private_key_jwt"]}
            """;
        var resolver = CreateResolver(out _, response: OkJson(body));

        var result = await resolver.ResolveAsync(ChatGptClientId);

        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task ResolveAsync_RejectsPluralAuthMethodsWithoutNone()
    {
        var body = $$"""
            {"client_id":"{{ChatGptClientId}}","client_name":"ChatGPT",
             "redirect_uris":["https://chatgpt.com/connector_platform_oauth_redirect"],
             "token_endpoint_auth_methods_supported":["private_key_jwt"]}
            """;
        var resolver = CreateResolver(out _, response: OkJson(body));

        (await resolver.ResolveAsync(ChatGptClientId)).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_RejectsEmptyPluralAuthMethods()
    {
        var body = $$"""
            {"client_id":"{{ChatGptClientId}}","client_name":"ChatGPT",
             "redirect_uris":["https://chatgpt.com/connector_platform_oauth_redirect"],
             "token_endpoint_auth_methods_supported":[]}
            """;
        var resolver = CreateResolver(out _, response: OkJson(body));

        (await resolver.ResolveAsync(ChatGptClientId)).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_RejectsMalformedPluralAuthMethods()
    {
        var body = $$"""
            {"client_id":"{{ChatGptClientId}}","client_name":"ChatGPT",
             "redirect_uris":["https://chatgpt.com/connector_platform_oauth_redirect"],
             "token_endpoint_auth_methods_supported":"none"}
            """;
        var resolver = CreateResolver(out _, response: OkJson(body));

        (await resolver.ResolveAsync(ChatGptClientId)).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_RejectsNullPluralAuthMethods()
    {
        var body = $$"""
            {"client_id":"{{ChatGptClientId}}","client_name":"ChatGPT",
             "redirect_uris":["https://chatgpt.com/connector_platform_oauth_redirect"],
             "token_endpoint_auth_methods_supported":null}
            """;
        var resolver = CreateResolver(out _, response: OkJson(body));

        (await resolver.ResolveAsync(ChatGptClientId)).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_AcceptsLegacyDocumentWithoutAuthMethod()
    {
        var resolver = CreateResolver(out _);

        (await resolver.ResolveAsync(ClientId)).ShouldNotBeNull();
    }

    [Fact]
    public async Task ResolveAsync_AcceptsLegacyDocumentWithNoneAuthMethod()
    {
        var body = ValidDocument.Replace("\"redirect_uris\":", "\"token_endpoint_auth_method\":\"none\",\"redirect_uris\":");
        var resolver = CreateResolver(out _, response: OkJson(body));

        (await resolver.ResolveAsync(ClientId)).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("private_key_jwt")]
    [InlineData("client_secret_post")]
    public async Task ResolveAsync_RejectsDocument_WithUnsupportedAuthMethod(string method)
    {
        var body = $$"""
            {"client_id": "{{ClientId}}", "client_name": "Claude Code",
             "redirect_uris": ["https://claude.ai/api/mcp/auth_callback"],
             "token_endpoint_auth_method": "{{method}}"}
            """;
        var resolver = CreateResolver(out _, response: OkJson(body));

        (await resolver.ResolveAsync(ClientId)).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_RejectsOversizedResponse()
    {
        // 64 KB cap (ADR §4.6) — a document padded past it must be rejected outright, not truncated.
        var oversized = new string('x', 70 * 1024);
        var body = $$"""
            {"client_id": "{{ClientId}}", "client_name": "{{oversized}}",
             "redirect_uris": ["https://claude.ai/api/mcp/auth_callback"]}
            """;
        var resolver = CreateResolver(out _, response: OkJson(body));

        (await resolver.ResolveAsync(ClientId)).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_RejectsNon2xxStatus()
    {
        var resolver = CreateResolver(out _, response: new HttpResponseMessage(HttpStatusCode.NotFound));

        (await resolver.ResolveAsync(ClientId)).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_RejectsWrongContentType()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ValidDocument, Encoding.UTF8, "text/html")
        };
        var resolver = CreateResolver(out _, response: response);

        (await resolver.ResolveAsync(ClientId)).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_RejectsMissingContentType()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(ValidDocument))
        };
        response.Content.Headers.ContentType = null;
        var resolver = CreateResolver(out _, response: response);

        (await resolver.ResolveAsync(ClientId)).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_RejectsRedirectResponse()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("https://internal.example.com/secret");

        var resolver = CreateResolver(out _, response: redirect);

        (await resolver.ResolveAsync(ClientId)).ShouldBeNull();
    }

    // ─── bounded state / concurrency (anonymous client_id flood) ────────────

    private static OAuthClientResolver CreateResolverFor(HttpMessageHandler handler) => new(
        new StubFactory(new HttpClient(handler)),
        new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new McpOAuthOptions { TrustedClientHosts = ["claude.ai"] }),
        NullLogger<OAuthClientResolver>.Instance);

    [Fact]
    public async Task ResolveAsync_ManyDistinctUnknownClientIds_BoundsConcurrencyAndLeavesNoInFlightState()
    {
        var handler = new SlowNotFoundHandler();
        var resolver = CreateResolverFor(handler);

        var results = await Task.WhenAll(Enumerable.Range(0, 1000)
            .Select(i => resolver.ResolveAsync($"https://claude.ai/oauth/random-{i}")));

        results.ShouldAllBe(r => r == null);
        handler.Calls.ShouldBe(1000);
        handler.MaxConcurrent.ShouldBeLessThanOrEqualTo(OAuthClientResolver.MaxConcurrentFetches);
        resolver.InFlightCount.ShouldBe(0); // single-flight map is emptied, not one entry per client_id
    }

    [Fact]
    public async Task ResolveAsync_SameClientIdConcurrently_SharesOneFetch()
    {
        var handler = new SlowNotFoundHandler();
        var resolver = CreateResolverFor(handler);

        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => resolver.ResolveAsync(ClientId)));

        handler.Calls.ShouldBe(1);
    }

    // ─── real DI-built HttpClient wiring ────────────────────────────────────

    [Fact]
    public async Task RealNamedHttpClient_BlocksLoopbackHost_ViaSsrfGuard()
    {
        using var provider = new ServiceCollection()
            .AddOAuthClientMetadataHttpClient().Services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        var resolver = new OAuthClientResolver(factory, new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new McpOAuthOptions { TrustedClientHosts = ["localhost"] }),
            NullLogger<OAuthClientResolver>.Instance);

        // Resolver level: allowlisted host, but the real ConnectCallback must refuse loopback.
        (await resolver.ResolveAsync("https://localhost/x")).ShouldBeNull();

        // Client level: prove the refusal comes from SsrfSafeConnect, not from e.g. a connection error.
        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => factory.CreateClient(OAuthClientResolver.HttpClientName).GetAsync("https://localhost/x"));
        ex.InnerException.ShouldBeOfType<SsrfBlockedException>();
    }

    [Fact]
    public void RealNamedHttpClient_HandlerDoesNotFollowRedirectsOrUseProxy()
    {
        using var provider = new ServiceCollection()
            .AddOAuthClientMetadataHttpClient().Services.BuildServiceProvider();

        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(OAuthClientResolver.HttpClientName);
        while (handler is DelegatingHandler d) handler = d.InnerHandler!;

        var sockets = handler.ShouldBeOfType<SocketsHttpHandler>();
        sockets.AllowAutoRedirect.ShouldBeFalse();
        sockets.UseProxy.ShouldBeFalse();
        sockets.ConnectCallback.ShouldNotBeNull();
    }

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    /// <summary>Answers 404 after a short delay and records the peak number of simultaneous requests.</summary>
    private sealed class SlowNotFoundHandler : HttpMessageHandler
    {
        private int _calls, _current, _max;
        public int Calls => _calls;
        public int MaxConcurrent => _max;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            var now = Interlocked.Increment(ref _current);
            int seen;
            while (now > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, seen) != seen) { }
            await Task.Delay(5, ct);
            Interlocked.Decrement(ref _current);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>Records every outgoing request and answers with a fixed response — no real network access.</summary>
    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(response);
        }
    }
}
