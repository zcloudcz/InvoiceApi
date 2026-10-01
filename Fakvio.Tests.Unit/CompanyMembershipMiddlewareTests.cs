using System.Security.Claims;
using Fakvio.API.Middleware;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Authentication;
using Fakvio.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Fakvio.Tests.Unit;
/// <summary>Isolated proposed middleware tests; no production registration is needed.</summary>
public class CompanyMembershipMiddlewareTests : IDisposable
{
    private readonly MasterDbContext _db = new(new DbContextOptionsBuilder<MasterDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    public CompanyMembershipMiddlewareTests()
    {
        _db.Client.AddRange(new Client { Id = 20, IsIssuer = true, IsActive = true, RegistrationNumber = "00000020" }, new Client { Id = 21, IsIssuer = true, IsActive = true, RegistrationNumber = "00000021" });
        _db.User.Add(new User { Id = 10, CompanyId = 20, Role = EUserRole.Admin });
        _db.SaveChanges();
    }
    private static DefaultHttpContext Request(string path = "/api/feedback", bool key = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "10"), new(ClaimTypes.Role, "Admin"), new("CompanyId", "20") };
        if (key) { claims.Add(new(ApiKeyAuthenticationDefaults.ScopeClaimType, "read,write")); claims.Add(new(ApiKeyAuthenticationDefaults.KeyIdClaimType, "30")); }
        return new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), Request = { Path = path } };
    }
    [Fact]
    public async Task RevokedDefaultCredential_CanDescribeItsExplicitAlternateGrants()
    {
        _db.UserCompanyMembership.Single().IsActive = false; await _db.SaveChangesAsync();
        var request = Request("/api/api-key/me", true); var called = false;
        await new CompanyMembershipMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(request, _db);
        called.ShouldBeTrue(); request.User.FindFirst("CompanyId").ShouldBeNull();
        request.User.IsInRole("Admin").ShouldBeFalse();
    }
    [Fact]
    public async Task PersistedSysAdminManualKey_PreservesLegacyImpersonation()
    {
        _db.User.Single().Role = EUserRole.SysAdmin; await _db.SaveChangesAsync();
        var request = Request(key: true); request.Request.Headers["X-Company-Id"] = "21"; var called = false;
        await new CompanyMembershipMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(request, _db);
        called.ShouldBeTrue(); request.User.IsInRole("SysAdmin").ShouldBeTrue();
    }    [Theory]
    [InlineData("/api/feedback")][InlineData("/api/notification")][InlineData("/api/company/billing")]
    public async Task RevokedMembership_DeniesAlsoMasterRoutes(string path)
    {
        _db.UserCompanyMembership.Single().IsActive = false; await _db.SaveChangesAsync();
        var request = Request(path); var called = false;
        await new CompanyMembershipMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(request, _db);
        request.Response.StatusCode.ShouldBe(403); called.ShouldBeFalse();
    }
    [Fact]
    public async Task RoleDowngrade_ReplacesStaleAdminBeforeNextMiddleware()
    {
        _db.UserCompanyMembership.Single().Role = EUserRole.User; await _db.SaveChangesAsync();
        var request = Request();
        await new CompanyMembershipMiddleware(ctx => { ctx.User.IsInRole("Admin").ShouldBeFalse(); ctx.User.IsInRole("User").ShouldBeTrue(); return Task.CompletedTask; }).InvokeAsync(request, _db);
        request.Response.StatusCode.ShouldBe(200);
    }
    [Theory]
    [InlineData("/api/my-companies")][InlineData("/api/my-companies/switch")][InlineData("/api/my-companies/invitations/accept")]
    public async Task RevokedCompany_AllowsIdentityOnlyRecovery(string path)
    {
        _db.UserCompanyMembership.RemoveRange(_db.UserCompanyMembership); await _db.SaveChangesAsync();
        var request = Request(path); var called = false;
        await new CompanyMembershipMiddleware(ctx => { called = true; ctx.User.FindFirst("CompanyId").ShouldBeNull(); ctx.User.IsInRole("Admin").ShouldBeFalse(); return Task.CompletedTask; }).InvokeAsync(request, _db);
        called.ShouldBeTrue();
    }
    [Theory]
    [InlineData("/api/auth/refresh")][InlineData("/api/my-companies/switch")][InlineData("/api/my-companies/invitations/accept")]
    public async Task MachineCredential_CannotMintInteractiveCredentials(string path)
    {
        var request = Request(path, true);
        await new CompanyMembershipMiddleware(_ => throw new Exception("Must not pass")).InvokeAsync(request, _db);
        request.Response.StatusCode.ShouldBe(403);
    }
    [Fact]
    public async Task ManualKey_CanSelectGrantedMembershipWhenDefaultWasRevoked()
    {
        _db.UserCompanyMembership.Single().IsActive = false;
        _db.UserCompanyMembership.Add(new() { UserId = 10, CompanyId = 21, Role = EUserRole.User });
        _db.ApiKey.Add(new() { Id = 30, UserId = 10, CompanyId = 20, AllowedCompanyIds = [20, 21] });
        await _db.SaveChangesAsync();
        var request = Request(key: true); request.Request.Headers["X-Selected-Company-Id"] = "21";
        await new CompanyMembershipMiddleware(ctx => { ctx.User.FindFirst("CompanyId")!.Value.ShouldBe("21"); ctx.User.IsInRole("User").ShouldBeTrue(); return Task.CompletedTask; }).InvokeAsync(request, _db);
        request.Response.StatusCode.ShouldBe(200);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task HeaderCannotBroadenGrantOrOAuthConsent(bool oauth)
    {
        _db.UserCompanyMembership.Add(new() { UserId = 10, CompanyId = 21, Role = EUserRole.User });
        _db.ApiKey.Add(new() { Id = 30, UserId = 10, CompanyId = 20, AllowedCompanyIds = [20] }); await _db.SaveChangesAsync();
        var request = Request(key: true); request.Request.Headers["X-Selected-Company-Id"] = "21";
        if (oauth) ((ClaimsIdentity)request.User.Identity!).AddClaim(new(ApiKeyAuthenticationDefaults.OAuthGrantIdClaimType, "1"));
        await new CompanyMembershipMiddleware(_ => throw new Exception("Must not pass")).InvokeAsync(request, _db);
        request.Response.StatusCode.ShouldBe(403);
    }
    public void Dispose() => _db.Dispose();
}
