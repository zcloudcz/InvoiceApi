using System.Net;
using System.Text.Json;
using Fakvio.Application.Service;
using Fakvio.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Controller-level coverage for <c>OAuthController</c> that does not need real PostgreSQL:
/// the feature-flag gate (T15 — "flag off = today's behaviour, unchanged") and the
/// "oauth-token" rate limiter (T13). Anything touching <c>IOAuthService</c>'s actual token
/// logic is in <see cref="OAuthServiceTests"/> against real PostgreSQL instead (see that
/// class's docs for why).
/// </summary>
public class OAuthControllerTests
{
    [Fact]
    public async Task WhenFlagIsOff_MetadataEndpointReturns404()
    {
        // Default FakvioFactory — McpOAuth:Enabled is not set, so it is false (ADR §5.1
        // default). This is the single most important regression to pin: today's behaviour
        // (no OAuth surface at all) must be exactly what an un-configured deployment gets.
        using var factory = new FakvioFactory();
        factory.InitializeDatabase();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/.well-known/oauth-authorization-server");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WhenFlagIsOff_TokenEndpointReturns404()
    {
        using var factory = new FakvioFactory();
        factory.InitializeDatabase();
        var client = factory.CreateClient();

        var response = await client.PostAsync("/oauth/token",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token" }));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WhenFlagIsOff_RevokeEndpointReturns404()
    {
        using var factory = new FakvioFactory();
        factory.InitializeDatabase();
        var client = factory.CreateClient();

        var response = await client.PostAsync("/oauth/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "whatever" }));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WhenFlagIsOn_MetadataEndpointDescribesTheIssuer()
    {
        using var factory = new EnabledOAuthFactory();
        factory.InitializeDatabase();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/.well-known/oauth-authorization-server");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("https://api.fakvio.test");
        body.ShouldContain("\"code_challenge_methods_supported\":[\"S256\"]");
    }

    [Fact]
    public async Task WhenFlagIsOn_TokenEndpoint_RejectsUnsupportedGrantType()
    {
        using var factory = new EnabledOAuthFactory();
        factory.InitializeDatabase();
        var client = factory.CreateClient();

        var response = await client.PostAsync("/oauth/token",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("unsupported_grant_type");
    }

    [Theory]
    [InlineData("authorization_code", "read,write", "read write")]
    [InlineData("refresh_token", "read,write", "read write")]
    [InlineData("authorization_code", "read", "read")]
    [InlineData("refresh_token", "read", "read")]
    public async Task TokenEndpoint_FormatsScopesUsingOAuthWireSyntax(string grantType, string storedScope, string expectedScope)
    {
        using var factory = new TokenResponseOAuthFactory();
        factory.InitializeDatabase();
        var expected = new OAuthTokenResult("access", "refresh", 3600, storedScope);
        factory.OAuthService.ExchangeAuthorizationCodeAsync(
                Arg.Any<ExchangeAuthorizationCodeRequest>(), Arg.Any<CancellationToken>())
            .Returns(expected);
        factory.OAuthService.RefreshAsync(Arg.Any<RefreshTokenRequest>(), Arg.Any<CancellationToken>())
            .Returns(expected);
        var client = factory.CreateClient();
        var fields = grantType == "authorization_code"
            ? new Dictionary<string, string>
            {
                ["grant_type"] = grantType,
                ["code"] = "code",
                ["redirect_uri"] = "https://chatgpt.com/callback",
                ["client_id"] = "https://chatgpt.com/client",
                ["code_verifier"] = "verifier"
            }
            : new Dictionary<string, string>
            {
                ["grant_type"] = grantType,
                ["refresh_token"] = "refresh"
            };

        var response = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(fields));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("scope").GetString().ShouldBe(expectedScope);
    }

    [Fact]
    public async Task TokenEndpoint_ExceedsRateLimit_Returns429()
    {
        using var factory = new TightOAuthRateLimitFactory();
        factory.InitializeDatabase();
        var client = factory.CreateClient();

        for (var i = 0; i < TightOAuthRateLimitFactory.PermitLimit; i++)
        {
            var response = await client.PostAsync("/oauth/token",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "bogus" }));
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest,
                $"request #{i + 1} of {TightOAuthRateLimitFactory.PermitLimit} is within the permit limit");
        }

        var rejected = await client.PostAsync("/oauth/token",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "bogus" }));

        rejected.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter.ShouldNotBeNull();
    }

    /// <summary>OAuth enabled, pointed at a fake-but-well-formed issuer/resource — no real client needed for these tests.</summary>
    public class EnabledOAuthFactory : FakvioFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("McpOAuth:Enabled", "true");
            builder.UseSetting("McpOAuth:Issuer", "https://api.fakvio.test");
            builder.UseSetting("McpOAuth:Resource", "https://mcp.fakvio.test/mcp");
        }
    }

    /// <summary>Replaces token persistence with a predictable result to test the HTTP wire format.</summary>
    private sealed class TokenResponseOAuthFactory : EnabledOAuthFactory
    {
        public IOAuthService OAuthService { get; } = Substitute.For<IOAuthService>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IOAuthService>();
                services.AddSingleton(OAuthService);
            });
        }
    }

    /// <summary>Same as <see cref="EnabledOAuthFactory"/>, with the "oauth-token" limiter dialed down to something a test can exhaust.</summary>
    public class TightOAuthRateLimitFactory : EnabledOAuthFactory
    {
        public const int PermitLimit = 3;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // Overrides the "1000000" the base factory sets for exactly this reason —
            // UseSetting applied later wins (same mechanism as TightRateLimitFactory).
            builder.UseSetting("RateLimiting:OAuthToken:PermitLimit", PermitLimit.ToString());
            builder.UseSetting("RateLimiting:OAuthToken:WindowSeconds", "60");
        }
    }
}
