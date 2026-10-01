using System.Security.Claims;
using Fakvio.Infrastructure.Authentication;
using System.ComponentModel.DataAnnotations;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.CompanyMembership;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;
public class CompanyMembershipServiceTests : IDisposable
{
    private readonly MasterDbContext _db = new(new DbContextOptionsBuilder<MasterDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly ICurrentUserService _user = Substitute.For<ICurrentUserService>();
    private readonly ITenantResolver _tenant = Substitute.For<ITenantResolver>();
    private readonly ITenantProvisioningService _provision = Substitute.For<ITenantProvisioningService>();
    private readonly CompanyMembershipService _service;
    private readonly DefaultHttpContext _http = new();
    public CompanyMembershipServiceTests()
    {
        _db.Client.Add(new Client { Id = 20, IsIssuer = true, IsActive = true, RegistrationNumber = "00000020", CompanyName = "Original" });
        _db.User.Add(new User { Id = 10, CompanyId = 20, Role = EUserRole.User, IsEmailVerified = true, Email = "member@example.test", PasswordHash = "unchanged" });
        _db.SaveChanges();
        _user.GetCurrentUserId().Returns(10);
        _service = new(_db, _user, _tenant, _provision, new HttpContextAccessor { HttpContext = _http }, NullLogger<CompanyMembershipService>.Instance);
    }
    private static CreateMyCompanyDto Create(Guid? operation = null) => new() { OperationId = operation ?? Guid.NewGuid(), CompanyName = "Additional", RegistrationNumber = "00000021" };
    [Fact]
    public async Task InvitationResend_InvalidatesOldTokenAndPreservesPassword()
    {
        _db.User.Add(new User { Id = 11, Email = "resend@test.cz", PasswordHash = "unchanged" });
        _db.User.Add(new User { Id = 12, Email = "sysadmin@test.cz", Role = EUserRole.SysAdmin });
        await _db.SaveChangesAsync();
        _user.GetCurrentUserId().Returns(12); _tenant.IsSysAdmin().Returns(true);
        var input = new InviteCompanyMemberDto { Email = "resend@test.cz", CompanyId = 20, Role = EUserRole.User };
        var old = await _service.InviteAsync(input);
        var current = await _service.InviteAsync(input);
        _user.GetCurrentUserId().Returns(11); _tenant.IsSysAdmin().Returns(false);
        await Should.ThrowAsync<ValidationException>(() => _service.AcceptAsync(old.Token!));
        (await _service.AcceptAsync(current.Token!)).CompanyId.ShouldBe(20);
        _db.User.Single(u => u.Id == 11).PasswordHash.ShouldBe("unchanged");
    }
    [Fact]
    public async Task MachineRetry_RequiresExplicitGrantForCreatedCompany()
    {
        var company = await _service.CreateAsync(Create());
        _db.ApiKey.Add(new ApiKey { Id = 30, UserId = 10, CompanyId = 20, AllowedCompanyIds = [20], KeyHash = "test", Name = "Test" });
        await _db.SaveChangesAsync();
        _http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ApiKeyAuthenticationDefaults.ScopeClaimType, "read,write"), new Claim(ApiKeyAuthenticationDefaults.KeyIdClaimType, "30") }, "ApiKey"));
        await Should.ThrowAsync<UnauthorizedAccessException>(() => _service.RetryProvisioningAsync(company.CompanyId));
        _db.ApiKey.Single().AllowedCompanyIds = [20, company.CompanyId]; await _db.SaveChangesAsync();
        (await _service.RetryProvisioningAsync(company.CompanyId)).CompanyId.ShouldBe(company.CompanyId);
    }
    [Fact]
    public async Task UnverifiedIdentity_CannotCreateAnotherCompany()
    {
        _db.User.Single().IsEmailVerified = false; await _db.SaveChangesAsync();
        await Should.ThrowAsync<UnauthorizedAccessException>(() => _service.CreateAsync(Create()));
        _db.Client.Count().ShouldBe(1);
    }
    [Fact]
    public async Task AddCompany_KeepsIdentityCredentialsAndDefault_AndRetriesSameOperation()
    {
        var input = Create();
        var first = await _service.CreateAsync(input);
        var retry = await _service.CreateAsync(input);
        retry.CompanyId.ShouldBe(first.CompanyId);
        _db.User.Count().ShouldBe(1);
        _db.User.Single().CompanyId.ShouldBe(20);
        _db.User.Single().PasswordHash.ShouldBe("unchanged");
        _db.UserCompanyMembership.Count().ShouldBe(2);
        first.Role.ShouldBe(EUserRole.Admin);
        first.IsProvisioned.ShouldBeFalse();
    }
    [Fact]
    public async Task RevokedMembership_IsNotRecreatedBySavingUser()
    {
        _db.UserCompanyMembership.RemoveRange(_db.UserCompanyMembership);
        await _db.SaveChangesAsync();
        _db.User.Single().FirstName = "Edited"; await _db.SaveChangesAsync();
        (await _service.ListAsync()).ShouldBeEmpty();
        _db.UserCompanyMembership.Count().ShouldBe(0);
    }
    [Fact]
    public async Task InvalidOperationId_IsRejected()
        => await Should.ThrowAsync<ValidationException>(() => _service.CreateAsync(Create(Guid.Empty)));
    [Fact]
    public async Task InviteAcceptance_IsIdentityBoundSingleUseAndDoesNotResetPassword()
    {
        var company = await _service.CreateAsync(Create());
        _db.User.Add(new User { Id = 11, Email = "invited@example.test", PasswordHash = "keep-me", IsActive = true });
        _db.User.Add(new User { Id = 12, Role = EUserRole.SysAdmin, Email = "admin@example.test" });
        await _db.SaveChangesAsync();
        _user.GetCurrentUserId().Returns(12); _tenant.IsSysAdmin().Returns(true);
        var invite = await _service.InviteAsync(new() { Email = "invited@example.test", CompanyId = company.CompanyId, Role = EUserRole.User });
        _db.CompanyMembershipInvitation.Single().TokenHash.ShouldNotBe(invite.Token);
        _user.GetCurrentUserId().Returns(10); _tenant.IsSysAdmin().Returns(false);
        await Should.ThrowAsync<UnauthorizedAccessException>(() => _service.AcceptAsync(invite.Token!));
        _user.GetCurrentUserId().Returns(11);
        var accepted = await _service.AcceptAsync(invite.Token!);
        accepted.CompanyId.ShouldBe(company.CompanyId);
        _db.User.Single(u => u.Id == 11).PasswordHash.ShouldBe("keep-me");
        _db.User.Count().ShouldBe(3);
        await Should.ThrowAsync<ValidationException>(() => _service.AcceptAsync(invite.Token!));
    }
    [Fact]
    public async Task TenantMemberCannotIssueInvitations()
        => await Should.ThrowAsync<UnauthorizedAccessException>(() => _service.InviteAsync(new() { Email = "member@example.test", CompanyId = 20 }));
    [Fact]
    public async Task RetryOtherCompany_IsRejected()
        => await Should.ThrowAsync<UnauthorizedAccessException>(() => _service.RetryProvisioningAsync(20));
    public void Dispose() => _db.Dispose();
}
