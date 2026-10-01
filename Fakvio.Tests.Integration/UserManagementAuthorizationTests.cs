using System.Net;
using System.Net.Http.Json;
using Fakvio.Contracts.Dto.User;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
namespace Fakvio.Tests.Integration;

/// <summary>Real JWT checks: Accountant keeps the Admin identifier, but cannot manage colleagues.</summary>
public class UserManagementAuthorizationTests : IClassFixture<FakvioFactory>
{
    private const long CompanyId = 8400, AccountantId = 8401, UserId = 8402, ColleagueId = 8403;
    private const string Password = "TestUser123", PendingToken = "authorization-pending-invitation";
    private readonly FakvioFactory _factory;
    public UserManagementAuthorizationTests(FakvioFactory factory)
    {
        _factory = factory;
        factory.InitializeDatabase();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        if (!db.Client.Any(c => c.Id == CompanyId))
        {
            db.Client.Add(new Client { Id = CompanyId, CompanyName = "Role checks", RegistrationNumber = "84848484", IsIssuer = true, IsActive = true });
            db.SaveChanges();
        }
        factory.SeedRegularUser("accountant-auth@example.com", AccountantId, CompanyId);
        factory.SeedRegularUser("user-auth@example.com", UserId, CompanyId);
        factory.SeedRegularUser("colleague-auth@example.com", ColleagueId, CompanyId);
        db.User.Single(u => u.Id == AccountantId).Role = EUserRole.Admin;
        var colleague = db.User.Single(u => u.Id == ColleagueId);
        colleague.IsInvitationPending = true;
        colleague.InvitationToken = PendingToken;
        colleague.InvitationTokenExpiresAt = DateTime.UtcNow.AddDays(1);
        db.SaveChanges();
    }
    public static IEnumerable<object[]> ForbiddenRequests()
    {
        foreach (var role in new[] { EUserRole.Admin, EUserRole.User })
        foreach (var operation in new[] { "list", "paged", "read", "missing", "create", "update", "delete", "invite", "token", "reset", "change" })
            yield return new object[] { role, operation };
    }
    [Theory]
    [MemberData(nameof(ForbiddenRequests))]
    public async Task TenantRole_CannotManageColleagues(EUserRole role, string operation)
    {
        using var client = await LoginAsync(role);
        using var response = await SendAsync(client, operation, ColleagueId);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{role}: {operation}");
    }
    [Theory]
    [InlineData(EUserRole.Admin, AccountantId)]
    [InlineData(EUserRole.User, UserId)]
    public async Task TenantRole_KeepsOwnProfilePasswordAndPreferences(EUserRole role, long id)
    {
        using var client = await LoginAsync(role);
        (await client.GetAsync($"/api/user/{id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsJsonAsync($"/api/user/{id}/change-password", new ChangePasswordDto { CurrentPassword = Password, NewPassword = Password })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PutAsJsonAsync("/api/user-preferences", new UserPreferencesDto { DefaultGridPageSize = 50 })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/api/user-preferences")).StatusCode.ShouldBe(HttpStatusCode.OK);
        // Resetting without the current password remains a SysAdmin operation, even for oneself.
        (await SendAsync(client, "reset", id)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
    [Theory]
    [InlineData("list", HttpStatusCode.OK)]
    [InlineData("paged", HttpStatusCode.OK)]
    [InlineData("read", HttpStatusCode.OK)]
    [InlineData("token", HttpStatusCode.OK)]
    [InlineData("create", HttpStatusCode.Created)]
    [InlineData("update", HttpStatusCode.OK)]
    [InlineData("invite", HttpStatusCode.Created)]
    [InlineData("reset", HttpStatusCode.OK)]
    public async Task SysAdmin_KeepsUserManagement(string operation, HttpStatusCode expected)
    {
        using var client = await LoginAsync(EUserRole.SysAdmin);
        using var response = await SendAsync(client, operation, ColleagueId);
        response.StatusCode.ShouldBe(expected);
        if (operation == "token") (await response.Content.ReadAsStringAsync()).ShouldContain(PendingToken);
    }
    [Fact]
    public async Task SysAdmin_CanDeleteNewUser()
    {
        using var client = await LoginAsync(EUserRole.SysAdmin);
        using var created = await SendAsync(client, "create", 0);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var user = await created.Content.ReadFromJsonAsync<UserDto>();
        (await client.DeleteAsync($"/api/user/{user!.Id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
    private async Task<HttpClient> LoginAsync(EUserRole role)
    {
        var client = _factory.CreateClient();
        var login = role == EUserRole.SysAdmin ? await AuthHelper.LoginAsSysAdminAsync(client)
            : await AuthHelper.LoginAsync(client, role == EUserRole.Admin ? "accountant-auth@example.com" : "user-auth@example.com", Password);
        AuthHelper.SetAuthToken(client, login.Token);
        return client;
    }
    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string operation, long id) => operation switch
    {
        "list" => client.GetAsync($"/api/user?companyId={CompanyId}"),
        "paged" => client.GetAsync($"/api/user/paged?companyId={CompanyId}"),
        "read" => client.GetAsync($"/api/user/{id}"),
        "missing" => client.GetAsync("/api/user/99999999"),
        "token" => client.GetAsync($"/api/user/{id}/invitation-token"),
        "delete" => client.DeleteAsync($"/api/user/{id}"),
        "create" => client.PostAsJsonAsync("/api/user", new CreateUserDto { Email = $"new-{Guid.NewGuid():N}@example.com", Password = Password, FirstName = "New", LastName = "User", CompanyId = CompanyId }),
        "invite" => client.PostAsJsonAsync("/api/user/invite", new InviteUserDto { Email = $"invite-{Guid.NewGuid():N}@example.com", FirstName = "New", LastName = "Invite", CompanyId = CompanyId }),
        "update" => client.PutAsJsonAsync($"/api/user/{id}", new UpdateUserDto { FirstName = "Updated" }),
        "reset" => client.PostAsJsonAsync($"/api/user/{id}/admin-reset-password", new AdminResetPasswordDto { NewPassword = Password }),
        "change" => client.PostAsJsonAsync($"/api/user/{id}/change-password", new ChangePasswordDto { CurrentPassword = Password, NewPassword = Password }),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };
}
