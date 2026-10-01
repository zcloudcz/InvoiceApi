using System.Net;
using System.Net.Http.Json;
using Fakvio.Contracts.Dto.Auth;
using Fakvio.Contracts.Dto.CompanyMembership;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>Real routing/JWT/middleware coverage for per-company administration.</summary>
public sealed class CompanyMembershipAdministrationEndpointTests : IClassFixture<FakvioFactory>
{
    private readonly FakvioFactory _factory;
    private const long Member = 8701, First = 8700, Second = 8702;
    public CompanyMembershipAdministrationEndpointTests(FakvioFactory factory)
    {
        _factory = factory;
        factory.InitializeDatabase();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        if (!db.Client.Any(c => c.Id == First))
        {
            db.Client.AddRange(new Client { Id = First, CompanyName = "First", RegistrationNumber = "87000000", IsIssuer = true },
                new Client { Id = Second, CompanyName = "Second", RegistrationNumber = "87000002", IsIssuer = true });
            db.SaveChanges();
        }
        factory.SeedRegularUser("membership-management@test.invalid", Member, First);
        if (!db.UserCompanyMembership.Any(m => m.UserId == Member && m.CompanyId == Second))
            db.UserCompanyMembership.Add(new() { UserId = Member, CompanyId = Second, Role = EUserRole.Admin });
        foreach (var membership in db.UserCompanyMembership.Where(m => m.UserId == Member))
        { membership.IsActive = true; membership.Role = membership.CompanyId == First ? EUserRole.User : EUserRole.Admin; }
        foreach (var id in new[] { First, Second })
            if (!db.CompanySystemSettings.Any(s => s.CompanyId == id))
                db.CompanySystemSettings.Add(new() { CompanyId = id, SchemaName = $"tenant_{id}", IsActive = true, IsProvisioned = true });
        db.SaveChanges();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TenantRolesCannotReadOrUpdateMemberships(bool accountant, bool update)
    {
        using var client = await MemberClient();
        if (accountant)
        {
            var response = await client.PostAsJsonAsync("/api/my-companies/switch", new SwitchCompanyDto { CompanyId = Second });
            response.EnsureSuccessStatusCode();
            AuthHelper.SetAuthToken(client, (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token);
        }
        using var result = update
            ? await client.PutAsJsonAsync($"/api/user/{Member}/memberships/{Second}", new UpdateCompanyMembershipDto { Role = EUserRole.User, IsActive = false })
            : await client.GetAsync($"/api/user/{Member}/memberships");
        result.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SysAdminRevocationIsEffectiveOnExistingSessionAndPreservesOtherCompany()
    {
        using var member = await MemberClient();
        var switched = await member.PostAsJsonAsync("/api/my-companies/switch", new SwitchCompanyDto { CompanyId = Second });
        switched.EnsureSuccessStatusCode();
        AuthHelper.SetAuthToken(member, (await switched.Content.ReadFromJsonAsync<LoginResponse>())!.Token);
        using var admin = _factory.CreateClient();
        AuthHelper.SetAuthToken(admin, (await AuthHelper.LoginAsSysAdminAsync(admin)).Token);
        var list = await admin.GetFromJsonAsync<List<ManagedCompanyMembershipDto>>($"/api/user/{Member}/memberships");
        list!.Count.ShouldBe(2);
        var revoke = await admin.PutAsJsonAsync($"/api/user/{Member}/memberships/{Second}",
            new UpdateCompanyMembershipDto { Role = EUserRole.User, IsActive = false });
        revoke.EnsureSuccessStatusCode();
        // A previously issued browser token cannot keep using the removed membership.
        (await member.GetAsync("/api/readiness")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var recovery = await member.PostAsJsonAsync("/api/my-companies/switch", new SwitchCompanyDto { CompanyId = First });
        recovery.EnsureSuccessStatusCode();
        AuthHelper.SetAuthToken(member, (await recovery.Content.ReadFromJsonAsync<LoginResponse>())!.Token);
        var remaining = await member.GetFromJsonAsync<List<CompanyMembershipDto>>("/api/my-companies");
        remaining!.Select(m => m.CompanyId).ShouldBe(new long[] { First });
    }

    [Fact]
    public async Task InvalidUpdateIs400AndUnknownMembershipIs404()
    {
        using var admin = _factory.CreateClient();
        AuthHelper.SetAuthToken(admin, (await AuthHelper.LoginAsSysAdminAsync(admin)).Token);
        (await admin.PutAsJsonAsync($"/api/user/{Member}/memberships/{Second}", new { })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync($"/api/user/{Member}/memberships/{Second}", new { Role = EUserRole.SysAdmin, IsActive = true })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync($"/api/user/{Member}/memberships/999999", new { Role = EUserRole.User, IsActive = false })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<HttpClient> MemberClient()
    {
        var client = _factory.CreateClient();
        AuthHelper.SetAuthToken(client, (await AuthHelper.LoginAsync(client, "membership-management@test.invalid", "TestUser123")).Token);
        return client;
    }
}
