using System.Net;
using Fakvio.McpServer.Client;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>
/// Tests the seam issue #239 leaves behind for the HTTP transport (#240):
/// <see cref="AuthHeaderHandler"/> wired the way <c>Program.cs</c> wires it —
/// through a real <see cref="IHttpClientFactory"/> pipeline.
///
/// <para>
/// Why this is a separate concern from <c>AuthHeaderHandlerTests</c>: those tests
/// construct the handler by hand, so they can only prove that the handler asks
/// the provider on every <c>SendAsync</c>. They cannot see the thing that makes
/// the registration subtle — <c>IHttpClientFactory</c> builds the whole handler
/// pipeline in its own private scope and POOLS it (default handler lifetime: 2
/// minutes). Whatever the provider is when the pipeline is first constructed is
/// what every later caller gets for the rest of that lifetime.
/// </para>
///
/// <para>
/// That is why DEVGUIDE §4.9 and <see cref="IApiTokenProvider"/> require every
/// implementation to be a SINGLETON that reads its credential from ambient
/// request-local state. These tests turn that prose rule into something that
/// fails out loud if the assumption behind it ever stops holding.
/// </para>
///
/// Junior note: nothing here talks to a network. The bottom of the pipeline is
/// <see cref="TokenEchoHandler"/>, which answers 200 OK with the bearer token it
/// saw — so "what went out on the wire" is simply the response body.
/// </summary>
public class AuthHeaderPipelineTests
{
    private const string ClientName = "fakvio-api";
    private const string RequestUrl = "https://test-api.fakvio.cz/api/invoice/1";

    /// <summary>
    /// The property #240 will build on: the pooled pipeline asks the provider on
    /// every request, not once when the pipeline is constructed.
    ///
    /// Both clients come out of the same factory inside one handler lifetime, so
    /// they share one pooled <see cref="AuthHeaderHandler"/> instance — and the
    /// two requests still carry two different tokens.
    /// </summary>
    [Fact]
    public async Task PooledPipeline_AsksTheProviderOnEveryRequest_NotOncePerPipelineConstruction()
    {
        var provider = Substitute.For<IApiTokenProvider>();
        provider.GetToken().Returns("token-user-a", "token-user-b");

        using var echo = new TokenEchoHandler();
        await using var services = BuildPipeline(echo, s => s.AddSingleton(provider));
        var factory = services.GetRequiredService<IHttpClientFactory>();

        // Two separate CreateClient calls — a new HttpClient each time, the same
        // pooled handler chain underneath.
        var firstToken = await SendAndReadTokenAsync(factory.CreateClient(ClientName));
        var secondToken = await SendAndReadTokenAsync(factory.CreateClient(ClientName));

        firstToken.ShouldBe("token-user-a");
        secondToken.ShouldBe("token-user-b");
        provider.Received(2).GetToken();
    }

    /// <summary>
    /// The shape DEVGUIDE §4.9 prescribes for multi-user hosting: ONE singleton
    /// provider serving concurrent callers, each reading its own token from
    /// ambient <c>AsyncLocal</c> state. Every request must come out carrying the
    /// token of the caller that issued it.
    ///
    /// All requests are held in flight simultaneously (see
    /// <see cref="TokenEchoHandler"/>), so this fails if the handler ever moves
    /// the token onto state shared between calls.
    /// </summary>
    [Fact]
    public async Task PooledPipeline_ConcurrentCallersWithAmbientTokens_DoNotBleedIntoEachOther()
    {
        const int concurrentCallers = 8;

        var provider = new AmbientApiTokenProvider();
        using var echo = new TokenEchoHandler(releaseAfter: concurrentCallers);
        await using var services = BuildPipeline(echo, s => s.AddSingleton<IApiTokenProvider>(provider));
        var factory = services.GetRequiredService<IHttpClientFactory>();

        // Each caller sets its own ambient token and then asserts on the token
        // that came back for ITS request — no shared bookkeeping to get wrong.
        var callers = Enumerable.Range(0, concurrentCallers).Select(caller => Task.Run(async () =>
        {
            var expected = $"token-caller-{caller}";
            provider.SetForCurrentContext(expected);

            var seen = await SendAndReadTokenAsync(factory.CreateClient(ClientName));

            return (Expected: expected, Seen: seen);
        }));

        var results = await Task.WhenAll(callers);

        results.ShouldAllBe(r => r.Seen == r.Expected);
    }

    /// <summary>
    /// Characterization test for the trap the registration rule exists to avoid:
    /// a SCOPED provider is captured by the pooled pipeline on first construction
    /// and then serves every later caller — one user's token on the next user's
    /// call, which is the same leak #239 removed from
    /// <c>HttpClient.DefaultRequestHeaders</c>, just one floor down.
    ///
    /// This is deliberately asserting the framework's behaviour, not ours: the
    /// "must be a singleton" rule in DEVGUIDE §4.9 is only true as long as this
    /// holds. If a future runtime aligned the scopes, this test would go red and
    /// tell us the rule can be relaxed — which is exactly what we want to hear.
    /// </summary>
    [Fact]
    public async Task ScopedProvider_IsCapturedByThePooledPipeline_WhichIsWhyRegistrationMustBeSingleton()
    {
        // One token per scope; each new provider instance takes the next one.
        var unusedTokens = new Queue<string>(["token-first-scope", "token-second-scope"]);

        using var echo = new TokenEchoHandler();
        await using var services = BuildPipeline(
            echo,
            s => s.AddScoped<IApiTokenProvider>(_ => new FixedApiTokenProvider(unusedTokens.Dequeue())));
        var factory = services.GetRequiredService<IHttpClientFactory>();

        string firstToken, secondToken;
        using (var firstScope = services.CreateScope())
        {
            firstToken = await SendAndReadTokenAsync(
                firstScope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName));
        }

        using (var secondScope = services.CreateScope())
        {
            secondToken = await SendAndReadTokenAsync(
                secondScope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName));
        }

        firstToken.ShouldBe("token-first-scope");
        secondToken.ShouldBe("token-first-scope", "the pooled pipeline kept the first scope's provider");
        unusedTokens.ShouldHaveSingleItem()
            .ShouldBe("token-second-scope", "the second scope's provider was never even constructed");
    }

    /// <summary>
    /// Mirrors the registration in <c>Fakvio.McpServer/Program.cs</c>: provider,
    /// handler as transient, handler appended to the client's pipeline. Only the
    /// provider lifetime differs between tests, so it is the parameter.
    /// </summary>
    private static ServiceProvider BuildPipeline(
        HttpMessageHandler primaryHandler, Action<IServiceCollection> registerTokenProvider)
    {
        var services = new ServiceCollection();
        registerTokenProvider(services);
        services.AddSingleton(new Fakvio.McpServer.Configuration.McpServerSettings());
        services.AddTransient<AuthHeaderHandler>();
        services.AddHttpClient(ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => primaryHandler)
            .AddHttpMessageHandler<AuthHeaderHandler>();

        return services.BuildServiceProvider();
    }

    /// <summary>Sends one request and returns the bearer token the pipeline put on it.</summary>
    private static async Task<string> SendAndReadTokenAsync(HttpClient client)
    {
        var response = await client.GetAsync(RequestUrl);

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Bottom of the pipeline: echoes back the bearer token it received instead
    /// of performing a real send.
    ///
    /// <paramref name="releaseAfter"/> holds every request until that many have
    /// arrived, so a concurrency test really has all of them in flight at once
    /// rather than accidentally running them one after another.
    /// </summary>
    private sealed class TokenEchoHandler(int releaseAfter = 1) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrived) >= releaseAfter)
            {
                _allArrived.TrySetResult();
            }

            await _allArrived.Task.WaitAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.Headers.Authorization?.Parameter ?? string.Empty)
            };
        }
    }

    /// <summary>
    /// Test double for the shape #240 has to use: a singleton that keeps no
    /// credential of its own and reads it from ambient request-local state.
    /// <c>AsyncLocal</c> stands in for what <c>IHttpContextAccessor</c> does.
    /// </summary>
    private sealed class AmbientApiTokenProvider : IApiTokenProvider
    {
        private readonly AsyncLocal<string?> _currentToken = new();

        public void SetForCurrentContext(string token) => _currentToken.Value = token;

        public string? GetToken() => _currentToken.Value;
    }

    /// <summary>Provider that hands back one fixed token — stands in for a per-scope credential.</summary>
    private sealed class FixedApiTokenProvider(string token) : IApiTokenProvider
    {
        public string? GetToken() => token;
    }
}
