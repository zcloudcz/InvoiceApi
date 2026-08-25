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

    /// <summary>Calls the readiness tool and returns the text the AI client would see.</summary>
    private static async Task<string> CallReadinessAsync(McpClient client)
    {
        var result = await client.CallToolAsync("get_readiness");

        return string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
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

        public static async Task<McpHttpTestHost> StartAsync(int releaseReadinessAfter = 1)
        {
            var api = new FakvioApiStub(releaseReadinessAfter);

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();

            // The one seam: every HttpClient the host creates ends up talking to the stub instead
            // of the network. This applies to clients registered before and after it, so the
            // production registration below stays exactly as it is in Program.cs.
            builder.Services.ConfigureHttpClientDefaults(http =>
                http.ConfigurePrimaryHttpMessageHandler(() => api));

            McpHttpHost.ConfigureServices(builder.Services, new McpServerSettings
            {
                ApiBaseUrl = "https://api.test.invalid"
            });

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

            return await McpClient.CreateAsync(transport);
        }

        /// <summary>
        /// Sends a bare <c>initialize</c> POST — the first thing any MCP client does — so a test can
        /// assert on the raw HTTP answer instead of on an exception thrown mid-handshake.
        /// </summary>
        public async Task<HttpResponseMessage> PostInitializeAsync(string? apiKey)
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

            if (apiKey is not null)
                request.Headers.Add("Authorization", $"Bearer {apiKey}");

            return await _transportClient.SendAsync(request);
        }

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

        /// <summary>How many times <c>GET /api/api-key/me</c> was asked — proves nothing is cached.</summary>
        public int IdentityCallCount => Volatile.Read(ref _identityCallCount);

        /// <summary>Makes the key unknown from the next request on, exactly like a revocation.</summary>
        public void Revoke(string apiKey) => _issuerByKey.TryRemove(apiKey, out _);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var presentedKey = request.Headers.Authorization?.Parameter;
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/api/api-key/me", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _identityCallCount);

                return IssuerOf(presentedKey) is null
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : Json(new ApiKeyIdentityDto { UserId = 1, Email = "machine@test.invalid", Scopes = "read" });
            }

            if (path.EndsWith("/api/readiness", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _readinessRequestsArrived) >= releaseReadinessAfter)
                    _allReadinessRequestsArrived.TrySetResult();

                await _allReadinessRequestsArrived.Task.WaitAsync(cancellationToken);

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
