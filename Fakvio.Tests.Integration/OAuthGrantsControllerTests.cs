using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using Fakvio.Contracts.Dto.OAuth;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Microsoft.AspNetCore.Hosting;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Controller-level coverage for "Připojené aplikace" (ADR 0001, docs/adr/0001-mcp-oauth21.md
/// §4.8, task N5.7) that does not need a live grant to revoke — the revoke path's atomicity
/// is <see cref="OAuthServiceTests"/>'s job against real PostgreSQL. Runs on the InMemory-backed
/// <see cref="FakvioFactory"/>: listing an empty/seeded set is a plain SELECT.
/// </summary>
public class OAuthGrantsControllerTests
{
    [Fact]
    public async Task WhenFlagIsOff_ListReturns404()
    {
        using var factory = new FakvioFactory();
        factory.InitializeDatabase();
        var email = "grants-flagoff@test.cz";
        var password = factory.SeedRegularUser(email, userId: 950, companyId: 20);
        SeedIssuer(factory, 20);
        var client = factory.CreateClient();
        var login = await AuthHelper.LoginAsync(client, email, password);
        AuthHelper.SetAuthToken(client, login.Token);

        var response = await client.GetAsync("/api/oauth/grants");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WhenFlagIsOn_ListReturnsEmptyForANewUser()
    {
        using var factory = new EnabledOAuthFactory();
        factory.InitializeDatabase();
        var email = "grants-owner@test.cz";
        var password = factory.SeedRegularUser(email, userId: 951, companyId: 21);
        SeedIssuer(factory, 21);
        var client = factory.CreateClient();
        var login = await AuthHelper.LoginAsync(client, email, password);
        AuthHelper.SetAuthToken(client, login.Token);

        var response = await client.GetAsync("/api/oauth/grants");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var grants = await response.Content.ReadFromJsonAsync<List<OAuthGrantDto>>();
        grants.ShouldBeEmpty();
    }

    [Fact]
    public async Task WhenFlagIsOn_ApiKeyCannotListGrants()
    {
        // ApiKeyRequestGuard denies any scope-claim principal on /api/oauth/grants* — same
        // rule as /api/api-key* (ADR §4.5).
        using var factory = new EnabledOAuthFactory();
        factory.InitializeDatabase();
        var email = "grants-apikey@test.cz";
        var password = factory.SeedRegularUser(email, userId: 952, companyId: 22);
        SeedIssuer(factory, 22);
        var client = factory.CreateClient();
        var login = await AuthHelper.LoginAsync(client, email, password);
        AuthHelper.SetAuthToken(client, login.Token);

        var created = await client.PostAsJsonAsync("/api/api-key", new { name = "test", scopes = "read" });
        var key = await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var rawKey = key.GetProperty("key").GetString();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", rawKey);

        var response = await client.GetAsync("/api/oauth/grants");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RevokingAnUnknownGrant_Returns404()
    {
        using var factory = new EnabledOAuthFactory();
        factory.InitializeDatabase();
        var email = "grants-revoke-unknown@test.cz";
        var password = factory.SeedRegularUser(email, userId: 953, companyId: 23);
        SeedIssuer(factory, 23);
        var client = factory.CreateClient();
        var login = await AuthHelper.LoginAsync(client, email, password);
        AuthHelper.SetAuthToken(client, login.Token);

        var response = await client.PostAsync("/api/oauth/grants/999999/revoke", null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static void SeedIssuer(FakvioFactory factory, long companyId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Fakvio.Infrastructure.Data.MasterDbContext>();
        db.Client.Add(new Fakvio.Domain.Entities.Client { Id = companyId, IsIssuer = true, CompanyName = "Grant test", RegistrationNumber = companyId.ToString() });
        db.SaveChanges();
    }
    private class EnabledOAuthFactory : FakvioFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("McpOAuth:Enabled", "true");
        }
    }
}
