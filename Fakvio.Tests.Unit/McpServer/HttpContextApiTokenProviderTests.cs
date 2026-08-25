using Fakvio.McpServer.Client;
using Microsoft.AspNetCore.Http;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>
/// Unit tests for the HTTP-mode credential source — the header parsing that
/// <see cref="McpHttpTransportTests"/> exercises end-to-end, pinned down here on the edges
/// that are awkward to drive through a real MCP client.
///
/// <para>
/// Every case below has to answer <c>null</c> rather than guess, because "no token" makes the
/// outgoing call unauthenticated and the API answers 401 — while a guess would send something
/// the API might accept on behalf of the wrong caller.
/// </para>
/// </summary>
public class HttpContextApiTokenProviderTests
{
    [Fact]
    public void BearerHeader_YieldsTheTokenItCarries()
    {
        var provider = ProviderFor("Bearer fak_live_key");

        provider.GetToken().ShouldBe("fak_live_key");
    }

    [Fact]
    public void BearerScheme_IsMatchedCaseInsensitively()
    {
        // HTTP auth schemes are case-insensitive; a client spelling it "bearer" is not an attacker.
        var provider = ProviderFor("bearer fak_live_key");

        provider.GetToken().ShouldBe("fak_live_key");
    }

    [Fact]
    public void NoAmbientRequest_YieldsNoToken()
    {
        // The shape that would appear if the transport ever ran tool calls outside the HTTP
        // request that carried them (a stateful session). It must fail closed, not fall back.
        var provider = new HttpContextApiTokenProvider(new HttpContextAccessor { HttpContext = null });

        provider.GetToken().ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer")]
    [InlineData("Bearer    ")]
    public void HeaderThatCarriesNoBearerToken_YieldsNoToken(string header)
    {
        var provider = ProviderFor(header);

        provider.GetToken().ShouldBeNull();
    }

    [Fact]
    public async Task ConcurrentRequests_EachGetTheirOwnToken()
    {
        // IHttpContextAccessor is a singleton over AsyncLocal, and so is the provider. This is the
        // unit-level statement of the rule in DEVGUIDE §4.9: one instance, many callers, no bleed.
        var accessor = new HttpContextAccessor();
        var provider = new HttpContextApiTokenProvider(accessor);

        var callers = Enumerable.Range(0, 8).Select(caller => Task.Run(() =>
        {
            var expected = $"fak_caller_{caller}";
            accessor.HttpContext = ContextWith($"Bearer {expected}");

            return (Expected: expected, Seen: provider.GetToken());
        }));

        var results = await Task.WhenAll(callers);

        results.ShouldAllBe(result => result.Seen == result.Expected);
    }

    private static HttpContextApiTokenProvider ProviderFor(string authorizationHeader) =>
        new(new HttpContextAccessor { HttpContext = ContextWith(authorizationHeader) });

    private static DefaultHttpContext ContextWith(string authorizationHeader)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = authorizationHeader;

        return context;
    }
}
