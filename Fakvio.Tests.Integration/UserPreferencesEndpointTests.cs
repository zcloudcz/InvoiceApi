using System.Net;
using System.Net.Http.Json;
using Fakvio.Contracts.Dto.User;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Integration tests for GET/PUT /api/user-preferences.
/// Exercises the full HTTP stack: JWT auth → controller → service → InMemory master DB.
/// The endpoints operate on the CURRENT user (SysAdmin from AuthHelper login).
/// </summary>
public class UserPreferencesEndpointTests : IClassFixture<FakvioFactory>
{
    private readonly FakvioFactory _factory;

    public UserPreferencesEndpointTests(FakvioFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);
        return client;
    }

    [Fact]
    public async Task Get_WithoutSavedPreferences_ReturnsDefaults()
    {
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/user-preferences");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<UserPreferencesDto>();
        dto.ShouldNotBeNull();
        dto!.DefaultGridPageSize.ShouldBe(10);
    }

    [Fact]
    public async Task Put_ThenGet_RoundTripsSavedValue()
    {
        var client = await CreateAuthenticatedClientAsync();

        var putResponse = await client.PutAsJsonAsync("/api/user-preferences",
            new UserPreferencesDto { DefaultGridPageSize = 50 });
        putResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var getResponse = await client.GetAsync("/api/user-preferences");
        var dto = await getResponse.Content.ReadFromJsonAsync<UserPreferencesDto>();
        dto!.DefaultGridPageSize.ShouldBe(50);
    }

    [Fact]
    public async Task Put_InvalidPageSize_Returns400()
    {
        var client = await CreateAuthenticatedClientAsync();

        var response = await client.PutAsJsonAsync("/api/user-preferences",
            new UserPreferencesDto { DefaultGridPageSize = 7 });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/user-preferences");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
