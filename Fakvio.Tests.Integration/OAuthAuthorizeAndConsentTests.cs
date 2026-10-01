using System.Net;
using System.Net.Http.Json;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.OAuth;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Controller-level coverage for the authorize + consent endpoints (ADR 0001,
/// docs/adr/0001-mcp-oauth21.md §4.2/§4.9, task N5.4), threats T1/T2/T8/T10/T15.
///
/// Runs on the InMemory-backed <see cref="FakvioFactory"/> — nothing exercised here needs
/// <c>ExecuteUpdateAsync</c>/<c>ExecuteDeleteAsync</c> (that is <see cref="OAuthServiceTests"/>
/// against real PostgreSQL); the CIMD fetch is replaced with a stub <see cref="IOAuthClientResolver"/>
/// so these tests never touch the network either.
/// </summary>
public class OAuthAuthorizeAndConsentTests
{
    private const string ClientId = "https://claude.ai/oauth/claude-code-client-metadata";
    private const string RedirectUri = "https://claude.ai/api/mcp/auth_callback";
    private const string ConsentUrlBase = "https://app.fakvio.test/oauth/consent";
    private const string CodeVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"; // RFC 7636 example
    private const string CodeChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"; // SHA256(CodeVerifier)

    private static Dictionary<string, string> AuthorizeQuery(string codeChallengeMethod = "S256", string? state = "xyz") => new()
    {
        ["response_type"] = "code",
        ["client_id"] = ClientId,
        ["redirect_uri"] = RedirectUri,
        ["code_challenge"] = CodeChallenge,
        ["code_challenge_method"] = codeChallengeMethod,
        ["state"] = state ?? ""
    };

    private static string BuildAuthorizeUrl(Dictionary<string, string> query)
        => "/oauth/authorize?" + string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

    // ─── Flag off (T15) ──────────────────────────────────────────────────────

    [Fact]
    public async Task WhenFlagIsOff_AuthorizeReturns404()
    {
        using var factory = new FakvioFactory();
        factory.InitializeDatabase();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync(BuildAuthorizeUrl(AuthorizeQuery()));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ─── client_id/redirect_uri errors never redirect (T2) ──────────────────

    [Fact]
    public async Task UnresolvableClientId_NeverRedirects()
    {
        using var factory = new StubResolverFactory(document: null);
        factory.InitializeDatabase();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync(BuildAuthorizeUrl(AuthorizeQuery()));

        response.StatusCode.ShouldNotBe(HttpStatusCode.Found);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task MismatchedRedirectUri_NeverRedirects()
    {
        using var factory = new StubResolverFactory(document: TrustedDocument());
        factory.InitializeDatabase();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var query = AuthorizeQuery();
        query["redirect_uri"] = "https://evil.example.com/callback";

        var response = await client.GetAsync(BuildAuthorizeUrl(query));

        response.StatusCode.ShouldNotBe(HttpStatusCode.Found);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ─── Everything else redirects with error+state+iss ─────────────────────

    [Fact]
    public async Task PlainCodeChallengeMethod_RedirectsWithInvalidRequest()
    {
        using var factory = new StubResolverFactory(document: TrustedDocument());
        factory.InitializeDatabase();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync(BuildAuthorizeUrl(AuthorizeQuery(codeChallengeMethod: "plain")));

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        var location = response.Headers.Location!.ToString();
        location.ShouldStartWith(RedirectUri);
        location.ShouldContain("error=invalid_request");
        location.ShouldContain("state=xyz");
        location.ShouldContain("iss=");
    }

    // ─── Happy path → consent redirect ───────────────────────────────────────

    [Fact]
    public async Task ValidRequest_RedirectsToConsentWithATicket()
    {
        using var factory = new StubResolverFactory(document: TrustedDocument());
        factory.InitializeDatabase();
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync(BuildAuthorizeUrl(AuthorizeQuery()));

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        var location = response.Headers.Location!.ToString();
        location.ShouldStartWith(ConsentUrlBase);
        location.ShouldContain("ticket=");
    }

    // ─── Consent requires a JWT session ──────────────────────────────────────

    [Fact]
    public async Task Consent_WithoutAuthentication_Returns401()
    {
        using var factory = new StubResolverFactory(document: TrustedDocument());
        factory.InitializeDatabase();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/oauth/consent/whatever");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Consent_WithApiKeyInsteadOfJwt_Returns403()
    {
        using var factory = new StubResolverFactory(document: TrustedDocument());
        factory.InitializeDatabase();
        var email = "consent-apikey@test.cz";
        var password = factory.SeedRegularUser(email, userId: 900, companyId: 10);
        SeedIssuer(factory, 10);
        var client = factory.CreateClient();
        var login = await AuthHelper.LoginAsync(client, email, password);
        AuthHelper.SetAuthToken(client, login.Token!);

        var created = await client.PostAsJsonAsync("/api/api-key", new { name = "test", scopes = "read" });
        var key = await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var rawKey = key.GetProperty("key").GetString();

        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", rawKey);

        var response = await client.GetAsync("/api/oauth/consent/whatever");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ─── Full happy path: authorize → consent describe → decide (allow) ────

    [Fact]
    public async Task FullFlow_Allow_RedirectsWithCode()
    {
        using var factory = new StubResolverFactory(document: TrustedDocument());
        factory.InitializeDatabase();
        var email = "consent-owner@test.cz";
        var password = factory.SeedRegularUser(email, userId: 901, companyId: 11);
        SeedIssuer(factory, 11);

        var anonClient = factory.CreateClient(new() { AllowAutoRedirect = false });
        var authorizeResponse = await anonClient.GetAsync(BuildAuthorizeUrl(AuthorizeQuery()));
        var location = authorizeResponse.Headers.Location!.ToString();
        var ticket = Uri.UnescapeDataString(location.Split("ticket=")[1]);

        var client = factory.CreateClient();
        var login = await AuthHelper.LoginAsync(client, email, password);
        AuthHelper.SetAuthToken(client, login.Token!);

        var describeResponse = await client.GetAsync($"/api/oauth/consent/{Uri.EscapeDataString(ticket)}");
        describeResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var info = await describeResponse.Content.ReadFromJsonAsync<OAuthConsentInfoDto>();
        info!.ClientId.ShouldBe(ClientId);
        info.IsEligible.ShouldBeTrue(); // AllowAll=true in StubResolverFactory
        info.CompanyId.ShouldBe(11);

        // Consent must describe the exact company on screen, even if another tab changed
        // the current session before this POST. A mismatched choice cannot issue a code.
        var mismatched = await client.PostAsJsonAsync("/api/oauth/consent/decision",
            new OAuthConsentDecisionDto { Ticket = ticket, Allow = true, Scope = "read", CompanyId = 12 });
        mismatched.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var decisionResponse = await client.PostAsJsonAsync("/api/oauth/consent/decision",
            new OAuthConsentDecisionDto { Ticket = ticket, Allow = true, Scope = "read", CompanyId = 11 });

        decisionResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await decisionResponse.Content.ReadFromJsonAsync<OAuthConsentDecisionResultDto>();
        result!.RedirectUrl.ShouldStartWith(RedirectUri);
        result.RedirectUrl.ShouldContain("code=");
    }

    [Fact]
    public async Task FullFlow_Deny_RedirectsWithAccessDenied()
    {
        using var factory = new StubResolverFactory(document: TrustedDocument());
        factory.InitializeDatabase();
        var email = "consent-denier@test.cz";
        var password = factory.SeedRegularUser(email, userId: 902, companyId: 12);
        SeedIssuer(factory, 12);

        var anonClient = factory.CreateClient(new() { AllowAutoRedirect = false });
        var authorizeResponse = await anonClient.GetAsync(BuildAuthorizeUrl(AuthorizeQuery()));
        var ticket = Uri.UnescapeDataString(authorizeResponse.Headers.Location!.ToString().Split("ticket=")[1]);

        var client = factory.CreateClient();
        var login = await AuthHelper.LoginAsync(client, email, password);
        AuthHelper.SetAuthToken(client, login.Token!);

        var decisionResponse = await client.PostAsJsonAsync("/api/oauth/consent/decision",
            new OAuthConsentDecisionDto { Ticket = ticket, Allow = false });

        var result = await decisionResponse.Content.ReadFromJsonAsync<OAuthConsentDecisionResultDto>();
        result!.RedirectUrl.ShouldContain("error=access_denied");
    }

    private static void SeedIssuer(FakvioFactory factory, long companyId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Fakvio.Infrastructure.Data.MasterDbContext>();
        db.Client.Add(new Fakvio.Domain.Entities.Client { Id = companyId, IsIssuer = true, CompanyName = "Consent test", RegistrationNumber = companyId.ToString() });
        db.SaveChanges();
    }
    private static OAuthClientDocument TrustedDocument()
        => new(ClientId, "Claude Code", [RedirectUri]);

    /// <summary>Enables OAuth and replaces the CIMD resolver with a stub — no network access.</summary>
    private class StubResolverFactory : FakvioFactory
    {
        private readonly OAuthClientDocument? _document;

        public StubResolverFactory(OAuthClientDocument? document) => _document = document;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.UseSetting("McpOAuth:Enabled", "true");
            builder.UseSetting("McpOAuth:AllowAll", "true");
            builder.UseSetting("McpOAuth:Issuer", "https://api.fakvio.test");
            builder.UseSetting("McpOAuth:Resource", "https://mcp.fakvio.test/mcp");
            builder.UseSetting("McpOAuth:ConsentUrl", ConsentUrlBase);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IOAuthClientResolver>();
                var resolver = Substitute.For<IOAuthClientResolver>();
                resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_document);
                services.AddSingleton(resolver);
            });
        }
    }
}
