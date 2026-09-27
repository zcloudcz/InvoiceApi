using System.Net;
using System.Text;
using Fakvio.Infrastructure.Authentication.OAuth;
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
    public async Task ResolveAsync_RejectsRedirectResponse()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("https://internal.example.com/secret");

        var resolver = CreateResolver(out _, response: redirect);

        (await resolver.ResolveAsync(ClientId)).ShouldBeNull();
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
