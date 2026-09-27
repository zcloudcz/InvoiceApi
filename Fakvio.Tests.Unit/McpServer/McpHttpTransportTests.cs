using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Fakvio.Contracts.Dto.ApiKey;
using Fakvio.Contracts.Dto.Readiness;
using Fakvio.McpServer.Configuration;
using Fakvio.McpServer.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>
/// End-to-end tests of the HTTP hosting mode (issue #240): a real MCP client talking the real
/// Streamable HTTP protocol to the real <see cref="McpHttpHost"/> configuration, over an
/// in-memory ASP.NET Core <c>TestServer</c>.
///
/// <para>
/// The only thing faked is the Fakvio REST API at the far end — <see cref="FakvioApiStub"/>
/// stands in for it and answers strictly on the credential presented. Everything in between
/// (the API-key gate, the singleton token provider, the pooled <c>IHttpClientFactory</c>
/// pipeline, the MCP transport) is production code, which is the point: the property under
/// test is a property of that wiring, and a test that rebuilt the wiring by hand could not
/// observe it.
/// </para>
///
/// <para>
/// Junior note: no sockets and no ports are involved. <c>TestServer</c> hands out an
/// <see cref="HttpClient"/> whose requests go straight into the ASP.NET Core pipeline.
/// </para>
/// </summary>
public class McpHttpTransportTests
{
    private const string TenantAKey = "fak_tenant_a_key";
    private const string TenantBKey = "fak_tenant_b_key";
    private const string TenantAIssuer = "Tenant A s.r.o.";
    private const string TenantBIssuer = "Tenant B GmbH";

    /// <summary>
    /// Upper bound on every wait in this file — the concurrency barrier in the stub, the MCP
    /// handshake and each tool call.
    ///
    /// <para>
    /// Nothing here touches a socket, so a wait that runs out has hit a broken build, not a slow
    /// machine. The bound is what makes that build <b>fail</b> the suite instead of hanging it:
    /// an unbounded barrier waits forever for a second request that a broken pipeline will never
    /// send, and a run that never finishes is a run nobody reads. Generous on purpose — it is a
    /// deadlock detector, not a performance assertion.
    /// </para>
    /// </summary>
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Header a <b>stateful</b> Streamable HTTP server returns on <c>initialize</c> to name the
    /// session the client must quote from then on. A stateless server never sends it.
    /// </summary>
    private const string McpSessionIdHeader = "Mcp-Session-Id";

    /// <summary>
    /// <b>The mandatory acceptance criterion of #240.</b> Two concurrent sessions belonging to
    /// different tenants must not leak data or credentials into each other.
    ///
    /// <para>
    /// Both tool calls are held inside the stub until both have arrived, so the sessions really
    /// overlap instead of running one after the other — overlap is when a shared credential
    /// shows up. Each session then has to see its own tenant's issuer name and nothing of the
    /// other's.
    /// </para>
    ///
    /// <para>
    /// This is the test that catches the registration mistake DEVGUIDE §4.9 forbids: with a
    /// scoped token provider the pooled HTTP pipeline captures one tenant's provider and serves
    /// both sessions with it, so one of the two answers comes back carrying the wrong tenant.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TwoConcurrentSessionsOfDifferentTenants_EachSeeOnlyItsOwnTenantsData()
    {
        // Neither readiness answer is released until both requests are in flight.
        await using var host = await McpHttpTestHost.StartAsync(releaseReadinessAfter: 2);

        var sessions = new[]
            {
                (Key: TenantAKey, Issuer: TenantAIssuer),
                (Key: TenantBKey, Issuer: TenantBIssuer)
            }
            .Select(async tenant =>
            {
                await using var client = await host.ConnectAsync(tenant.Key);
                var answer = await CallReadinessAsync(client);

                return (tenant.Issuer, Answer: answer);
            });

        var results = await Task.WhenAll(sessions);

        foreach (var (issuer, answer) in results)
        {
            answer.ShouldContain(issuer, Case.Sensitive,
                "the session got an answer that is not its own tenant's readiness report");

            var otherTenantsIssuer = issuer == TenantAIssuer ? TenantBIssuer : TenantAIssuer;
            answer.ShouldNotContain(otherTenantsIssuer, Case.Sensitive,
                "the other tenant's data leaked into this session's answer");
        }
    }

    /// <summary>
    /// A client presenting a key the API accepts gets the full tool surface — the same tools the
    /// stdio server exposes, because both hosts scan the same assembly.
    /// </summary>
    [Fact]
    public async Task ValidApiKey_GetsTheFullToolListOverHttp()
    {
        await using var host = await McpHttpTestHost.StartAsync();
        await using var client = await host.ConnectAsync(TenantAKey);

        var tools = await client.ListToolsAsync();

        tools.ShouldNotBeEmpty();
        tools.Select(tool => tool.Name).ShouldContain("get_readiness");
        client.ServerInfo.Name.ShouldBe("fakvio");
    }

    /// <summary>
    /// A tool call over HTTP reaches the API carrying the caller's own credential, and its answer
    /// comes back to the AI client. This is the "working tool calls" half of the acceptance
    /// criteria; the isolation test above covers the "and only yours" half.
    /// </summary>
    [Fact]
    public async Task ValidApiKey_CanCallAToolAndGetsItsOwnTenantsAnswer()
    {
        await using var host = await McpHttpTestHost.StartAsync();
        await using var client = await host.ConnectAsync(TenantBKey);

        var answer = await CallReadinessAsync(client);

        answer.ShouldContain(TenantBIssuer);
    }

    /// <summary>
    /// A key the API does not accept never reaches the MCP transport — the gate answers 401.
    /// Asserted at the HTTP level rather than through the MCP client, because that is the
    /// contract an AI client sees before any protocol handshake happens.
    /// </summary>
    [Fact]
    public async Task InvalidApiKey_IsRejectedWith401()
    {
        await using var host = await McpHttpTestHost.StartAsync();

        using var response = await host.PostInitializeAsync("fak_this_key_does_not_exist");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldContain("Bearer");
    }

    /// <summary>
    /// No credential at all is refused without spending an API round-trip on it — the gate can
    /// answer that one by itself, and an unauthenticated caller must not be able to make the MCP
    /// server hammer the API.
    /// </summary>
    [Fact]
    public async Task MissingAuthorizationHeader_IsRejectedWith401_WithoutAskingTheApi()
    {
        await using var host = await McpHttpTestHost.StartAsync();

        using var response = await host.PostInitializeAsync(apiKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        host.Api.IdentityCallCount.ShouldBe(0);
    }

    /// <summary>
    /// Revocation takes effect on the next request, which is what story #144 asks for: the gate
    /// re-asks <c>GET /api/api-key/me</c> every time and caches nothing. A cached answer would
    /// keep a revoked key alive for as long as the cache lived.
    /// </summary>
    [Fact]
    public async Task RevokedApiKey_StopsWorkingOnTheVeryNextRequest()
    {
        await using var host = await McpHttpTestHost.StartAsync();

        using (var accepted = await host.PostInitializeAsync(TenantAKey))
        {
            accepted.IsSuccessStatusCode.ShouldBeTrue();
        }

        host.Api.Revoke(TenantAKey);

        using var refused = await host.PostInitializeAsync(TenantAKey);
        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// An <c>Authorization</c> header the caller filled in with something that is not a usable
    /// bearer token is refused, whichever way it is malformed.
    ///
    /// <para>
    /// The gate deliberately knows nothing about what a valid key looks like - it asks the API.
    /// That makes "wrong scheme" and "empty token" cases interesting: the token provider hands
    /// the outbound pipeline nothing, so the API is asked <i>unauthenticated</i> and refuses, and
    /// the caller must see 401 rather than reach a tool. Written against the outcome, not against
    /// how the gate arrives at it, so it keeps holding if the gate ever short-circuits these
    /// locally instead of spending an API round-trip on them.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Basic dXNlcjpwYXNz")]           // wrong scheme entirely
    [InlineData("Bearer")]                       // scheme, no token
    [InlineData("Bearer    ")]                   // scheme, whitespace token
    [InlineData("fak_tenant_a_key")]             // real key, no scheme
    [InlineData("!!! not a header at all !!!")]  // garbage
    public async Task AuthorizationHeaderCarryingNoUsableToken_IsRejectedWith401(string header)
    {
        await using var host = await McpHttpTestHost.StartAsync();

        using var response = await host.PostInitializeWithAuthorizationAsync(header);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized,
            $"Authorization: '{header}' reached the MCP transport instead of being refused.");
    }

    /// <summary>
    /// The transport really runs stateless, asserted on what the protocol shows rather than on
    /// the configuration line that causes it.
    ///
    /// <para>
    /// A stateful server answers <c>initialize</c> with an <c>Mcp-Session-Id</c> header and then
    /// expects it back on every follow-up; a stateless one issues none, because there is no
    /// session to address. Its absence is therefore the observable form of
    /// <c>SessionMode = Stateless</c> in <c>McpHttpHost</c> - verified by flipping that line to
    /// <c>Stateful</c>, which makes the header appear and this test fail.
    /// </para>
    ///
    /// <para>
    /// Why it is worth pinning: reading the caller's token off <c>HttpContext</c> only works
    /// while the tool handler runs on the execution context of the request that carried it, and
    /// stateless is what guarantees that. It also means no session affinity, so the host scales
    /// out without sticky routing (#241). Both properties are lost silently by editing one word.
    /// </para>
    /// </summary>
    [Fact]
    public async Task StreamableHttpTransport_RunsStateless_AndIssuesNoSessionId()
    {
        await using var host = await McpHttpTestHost.StartAsync();

        using var response = await host.PostInitializeAsync(TenantAKey);

        response.IsSuccessStatusCode.ShouldBeTrue();
        response.Headers.Contains(McpSessionIdHeader).ShouldBeFalse(
            "The server handed out a session id, so it is no longer running stateless: tool calls " +
            "may execute outside the HTTP request that carried them (no HttpContext, no caller " +
            "token) and the host now needs sticky routing to scale out.");
    }

    /// <summary>
    /// The gate guards the whole pipeline, not just <c>/mcp</c>. An unauthenticated request to a
    /// path that maps to nothing is answered 401, not 404.
    ///
    /// <para>
    /// This host serves nothing but MCP, so closed-by-default is the cheap correct posture: a
    /// route added later is protected the moment it exists, instead of being open until somebody
    /// notices. Registering the gate on the MCP route only would flip that, and nothing else in
    /// this suite would go red.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RequestOutsideTheMcpEndpoint_IsGatedToo()
    {
        await using var host = await McpHttpTestHost.StartAsync();

        using var response = await host.GetAsync("/some/route/added/later");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A key the API refuses with 403 (authenticated, but not allowed) reaches the MCP client as
    /// a 401 - not as a server error.
    ///
    /// <para>
    /// <c>FakvioApiClient.GetIdentityAsync</c> treats 401 and 403 alike: both are the API having
    /// looked at the credential and said no, which is a domain answer rather than a failure. Drop
    /// the 403 arm and this path would throw instead, turning "your key lacks the scope" into a
    /// 500 that sends the operator looking for an outage.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ApiRefusingTheKeyWithForbidden_IsRejectedWith401_NotAServerError()
    {
        await using var host = await McpHttpTestHost.StartAsync();
        host.Api.IdentityStatusOverride = HttpStatusCode.Forbidden;

        using var response = await host.PostInitializeAsync(TenantAKey);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// An unreachable API is <b>not</b> reported as a bad credential. The failure escapes the
    /// gate, which under a real Kestrel host renders as 500.
    ///
    /// <para>
    /// This is the deliberate design call of #240: "the API is unreachable" and "your key is
    /// invalid" are different diagnoses, and answering 401 to the first one sends the operator
    /// hunting through key management while the API is down. The test pins the absence of a
    /// <c>catch</c> in the gate - add one that falls back to 401 and it goes red.
    /// </para>
    ///
    /// <para>
    /// Junior note: the assertion is an exception rather than a 500 status because
    /// <c>TestServer</c> hands an unhandled pipeline exception straight back to the caller
    /// instead of rendering an error response. The property under test - the failure is not
    /// converted into 401 - is the same either way.
    /// </para>
    /// </summary>
    [Fact]
    public async Task UnreachableApi_DoesNotBecomeA401()
    {
        await using var host = await McpHttpTestHost.StartAsync();
        host.Api.IsUnreachable = true;

        await Should.ThrowAsync<HttpRequestException>(() => host.PostInitializeAsync(TenantAKey));
    }

    /// <summary>Calls the readiness tool and returns the text the AI client would see.</summary>
    private static async Task<string> CallReadinessAsync(McpClient client)
    {
        // Bounded for the same reason as the barrier it ends up waiting on: a tool call that
        // never comes back must fail this test, not stall the whole suite.
        using var deadline = new CancellationTokenSource(WaitBudget);

        var result = await client.CallToolAsync("get_readiness", cancellationToken: deadline.Token);

        return string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
    }

    // ─── MCP OAuth 2.1 (ADR 0001, task N5.6) ────────────────────────────────

    private static McpServerSettings OAuthSettings() => new()
    {
        OAuthEnabled = true,
        PublicUrl = "https://mcp.fakvio.test",
        OAuthIssuer = "https://api.fakvio.test"
    };

    /// <summary>Flag off — today's PRM (none at all) is unchanged (T15).</summary>
    [Fact]
    public async Task WhenOAuthDisabled_ProtectedResourceMetadataIs404()
    {
        await using var host = await McpHttpTestHost.StartAsync();

        var response = await host.GetAsync("/.well-known/oauth-protected-resource/mcp");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>Flag off — the challenge is the bare "Bearer" it always was (T15).</summary>
    [Fact]
    public async Task WhenOAuthDisabled_ChallengeHasNoResourceMetadata()
    {
        await using var host = await McpHttpTestHost.StartAsync();

        using var response = await host.PostInitializeAsync(apiKey: null);

        response.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
    }

    [Theory]
    [InlineData("/.well-known/oauth-protected-resource/mcp")]
    [InlineData("/.well-known/oauth-protected-resource")]
    public async Task WhenOAuthEnabled_ProtectedResourceMetadata_IsServedWithoutAuth_OnBothPaths(string path)
    {
        await using var host = await McpHttpTestHost.StartAsync(settings: OAuthSettings());

        var response = await host.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("\"resource\":\"https://mcp.fakvio.test/mcp\"");
        body.ShouldContain("\"authorization_servers\":[\"https://api.fakvio.test\"]");
    }

    [Fact]
    public async Task WhenOAuthEnabled_UnauthenticatedRequestToAnotherPath_Still401s()
    {
        // PRM is the one deliberate exception — everything else stays gated exactly as before.
        await using var host = await McpHttpTestHost.StartAsync(settings: OAuthSettings());

        using var response = await host.PostInitializeAsync(apiKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WhenOAuthEnabled_ChallengeAdvertisesResourceMetadataAndScope()
    {
        await using var host = await McpHttpTestHost.StartAsync(settings: OAuthSettings());

        using var response = await host.PostInitializeAsync(apiKey: null);

        var challenge = response.Headers.WwwAuthenticate.ToString();
        challenge.ShouldContain("resource_metadata=\"https://mcp.fakvio.test/.well-known/oauth-protected-resource/mcp\"");
        challenge.ShouldContain("scope=\"read write\"");
    }

    /// <summary>T6 — an OAuth token minted for a DIFFERENT resource server must not authenticate here.</summary>
    [Fact]
    public async Task WhenOAuthEnabled_TokenWithWrongResource_IsRejectedWith401()
    {
        await using var host = await McpHttpTestHost.StartAsync(settings: OAuthSettings());
        const string wrongResourceKey = "fak_oat_wrong_resource";
        host.Api.RegisterOAuthToken(wrongResourceKey, "Wrong Resource Ltd", "https://someone-elses-mcp.example.com/mcp");

        using var response = await host.PostInitializeAsync(wrongResourceKey);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>The matching-resource counterpart — an OAuth token minted for THIS server authenticates normally.</summary>
    [Fact]
    public async Task WhenOAuthEnabled_TokenWithMatchingResource_Authenticates()
    {
        await using var host = await McpHttpTestHost.StartAsync(settings: OAuthSettings());
        const string rightResourceKey = "fak_oat_right_resource";
        host.Api.RegisterOAuthToken(rightResourceKey, "Right Resource s.r.o.", "https://mcp.fakvio.test/mcp");

        await using var client = await host.ConnectAsync(rightResourceKey);
        var answer = await CallReadinessAsync(client);

        answer.ShouldContain("Right Resource s.r.o.");
    }

    /// <summary>A manually created API key has no resource binding at all and is exempt from the audience check (ADR §4.4).</summary>
    [Fact]
    public async Task WhenOAuthEnabled_PlainApiKeyWithNoOAuthResource_StillAuthenticates()
    {
        await using var host = await McpHttpTestHost.StartAsync(settings: OAuthSettings());

        await using var client = await host.ConnectAsync(TenantAKey);
        var answer = await CallReadinessAsync(client);

        answer.ShouldContain(TenantAIssuer);
    }

    /// <summary>
    /// The MCP HTTP server under test, hosted in memory, with <see cref="FakvioApiStub"/> wired in
    /// as the far end of the outbound HTTP pipeline.
    /// </summary>
    private sealed class McpHttpTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _transportClient;

        private McpHttpTestHost(WebApplication app, FakvioApiStub api, HttpClient transportClient)
        {
            _app = app;
            _transportClient = transportClient;
            Api = api;
        }

        /// <summary>The stand-in REST API, so a test can revoke a key or count its calls.</summary>
        public FakvioApiStub Api { get; }

        public static async Task<McpHttpTestHost> StartAsync(int releaseReadinessAfter = 1, McpServerSettings? settings = null)
        {
            var api = new FakvioApiStub(releaseReadinessAfter);

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();

            // The one seam: every HttpClient the host creates ends up talking to the stub instead
            // of the network. This applies to clients registered before and after it, so the
            // production registration below stays exactly as it is in Program.cs.
            builder.Services.ConfigureHttpClientDefaults(http =>
                http.ConfigurePrimaryHttpMessageHandler(() => api));

            settings ??= new McpServerSettings();
            settings.ApiBaseUrl = "https://api.test.invalid";

            McpHttpHost.ConfigureServices(builder.Services, settings);

            var app = builder.Build();
            McpHttpHost.MapEndpoints(app);

            await app.StartAsync();

            return new McpHttpTestHost(app, api, app.GetTestClient());
        }

        /// <summary>Connects a real MCP client over Streamable HTTP, presenting the given API key.</summary>
        public async Task<McpClient> ConnectAsync(string apiKey)
        {
            var options = new HttpClientTransportOptions
            {
                Endpoint = new Uri(_transportClient.BaseAddress!, McpHttpHost.EndpointPath),
                TransportMode = HttpTransportMode.StreamableHttp,

                // The server runs stateless, where GET /mcp is not available (there is no session
                // to push unsolicited messages into). Asking for that stream would only 405.
                EnableStandaloneGetStream = false,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" }
            };

            var transport = new HttpClientTransport(options, _transportClient);

            using var deadline = new CancellationTokenSource(WaitBudget);

            return await McpClient.CreateAsync(transport, cancellationToken: deadline.Token);
        }

        /// <summary>
        /// Sends a bare <c>initialize</c> POST — the first thing any MCP client does — so a test can
        /// assert on the raw HTTP answer instead of on an exception thrown mid-handshake.
        /// </summary>
        public Task<HttpResponseMessage> PostInitializeAsync(string? apiKey) =>
            PostInitializeWithAuthorizationAsync(apiKey is null ? null : $"Bearer {apiKey}");

        /// <summary>
        /// The same request, but with the <c>Authorization</c> header written out verbatim - for
        /// the cases where the malformed header itself is what is under test.
        /// </summary>
        public async Task<HttpResponseMessage> PostInitializeWithAuthorizationAsync(string? authorizationHeader)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, McpHttpHost.EndpointPath)
            {
                Content = JsonContent.Create(new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "initialize",
                    @params = new
                    {
                        protocolVersion = "2025-06-18",
                        capabilities = new { },
                        clientInfo = new { name = "test-client", version = "1.0.0" }
                    }
                })
            };

            request.Headers.Accept.Add(new("application/json"));
            request.Headers.Accept.Add(new("text/event-stream"));

            // TryAddWithoutValidation, because half the point of the theory above is to send
            // header values that HttpClient would otherwise refuse to put on the wire.
            if (authorizationHeader is not null)
                request.Headers.TryAddWithoutValidation("Authorization", authorizationHeader);

            return await _transportClient.SendAsync(request);
        }

        /// <summary>A plain unauthenticated GET, for asserting what the gate does off the MCP route.</summary>
        public Task<HttpResponseMessage> GetAsync(string path) => _transportClient.GetAsync(path);

        public async ValueTask DisposeAsync()
        {
            _transportClient.Dispose();
            await _app.DisposeAsync();
            Api.Dispose();
        }
    }

    /// <summary>
    /// Stand-in for the Fakvio REST API. Knows two API keys, one per tenant, and answers strictly
    /// on the credential presented — which is what makes a leak visible: an answer carrying the
    /// wrong tenant's issuer name can only mean the wrong token went out.
    ///
    /// <paramref name="releaseReadinessAfter"/> holds every readiness request until that many have
    /// arrived, so a concurrency test really has them all in flight at once.
    /// </summary>
    private sealed class FakvioApiStub(int releaseReadinessAfter) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, string> _issuerByKey = new()
        {
            [TenantAKey] = TenantAIssuer,
            [TenantBKey] = TenantBIssuer
        };

        private readonly TaskCompletionSource _allReadinessRequestsArrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _readinessRequestsArrived;
        private int _identityCallCount;

        /// <summary>
        /// Status the identity endpoint answers with regardless of the key presented.
        /// Null = normal behaviour (look the key up).
        /// </summary>
        public HttpStatusCode? IdentityStatusOverride { get; set; }

        /// <summary>Makes every call fail at the transport level, exactly like an API that is down.</summary>
        public bool IsUnreachable { get; set; }

        /// <summary>How many times <c>GET /api/api-key/me</c> was asked — proves nothing is cached.</summary>
        public int IdentityCallCount => Volatile.Read(ref _identityCallCount);

        /// <summary>Makes the key unknown from the next request on, exactly like a revocation.</summary>
        public void Revoke(string apiKey) => _issuerByKey.TryRemove(apiKey, out _);

        /// <summary>
        /// Registers <paramref name="apiKey"/> as an OAuth-issued access token bound to
        /// <paramref name="resource"/> — the identity response then carries OAuthGrantId/
        /// OAuthResource, which is what the gate's audience check (T6) reads.
        /// </summary>
        public void RegisterOAuthToken(string apiKey, string issuer, string resource)
        {
            _issuerByKey[apiKey] = issuer;
            _oauthResourceByKey[apiKey] = resource;
        }

        private readonly ConcurrentDictionary<string, string> _oauthResourceByKey = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Not an HTTP status: the API is not answering at all. HttpClient surfaces that shape
            // as HttpRequestException, which is what a refused connection looks like in production.
            if (IsUnreachable)
                throw new HttpRequestException("The Fakvio API is unreachable.");

            var presentedKey = request.Headers.Authorization?.Parameter;
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/api/api-key/me", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _identityCallCount);

                if (IdentityStatusOverride is { } forcedStatus)
                    return new HttpResponseMessage(forcedStatus);

                if (IssuerOf(presentedKey) is null)
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);

                var oauthResource = presentedKey is not null && _oauthResourceByKey.TryGetValue(presentedKey, out var resource) ? resource : null;

                return Json(new ApiKeyIdentityDto
                {
                    UserId = 1,
                    Email = "machine@test.invalid",
                    Scopes = "read",
                    OAuthGrantId = oauthResource is null ? null : 1,
                    OAuthResource = oauthResource
                });
            }

            if (path.EndsWith("/api/readiness", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _readinessRequestsArrived) >= releaseReadinessAfter)
                    _allReadinessRequestsArrived.TrySetResult();

                // Bounded wait: if the other session never arrives because the pipeline is broken,
                // this throws and the test reports a failure instead of blocking forever.
                await _allReadinessRequestsArrived.Task.WaitAsync(WaitBudget, cancellationToken);

                var issuer = IssuerOf(presentedKey);

                return issuer is null
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : Json(new ReadinessReportDto
                    {
                        Issues = [new ReadinessIssueDto { Code = "BankAccountMissing", IssuerName = issuer }]
                    });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private string? IssuerOf(string? apiKey) =>
            apiKey is not null && _issuerByKey.TryGetValue(apiKey, out var issuer) ? issuer : null;

        private static HttpResponseMessage Json<T>(T payload) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };
    }
}
