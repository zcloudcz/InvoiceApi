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

/// <summary>Checks persisted authorization and that one-company changes cannot mutate other access.</summary>
public sealed class CompanyMembershipAdministrationTests : IDisposable
{
    private readonly MasterDbContext _db = new(new DbContextOptionsBuilder<MasterDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly ICurrentUserService _actor = Substitute.For<ICurrentUserService>();
    private readonly ITenantResolver _tenant = Substitute.For<ITenantResolver>();
    private readonly CompanyMembershipService _service;

    public CompanyMembershipAdministrationTests()
    {
        _db.Client.AddRange(new Client { Id = 20, CompanyName = "Default", RegistrationNumber = "20", IsIssuer = true },
            new Client { Id = 21, CompanyName = "Secondary", RegistrationNumber = "21", IsIssuer = true });
        _db.User.AddRange(new User { Id = 10, Email = "member@test.invalid", CompanyId = 20, Role = EUserRole.User, PasswordHash = "unchanged" },
            new User { Id = 11, Email = "admin@test.invalid", Role = EUserRole.SysAdmin });
        _db.UserCompanyMembership.Add(new() { UserId = 10, CompanyId = 21, Role = EUserRole.Admin });
        _db.ApiKey.Add(new() { Id = 30, UserId = 10, CompanyId = 20, AllowedCompanyIds = [20], Name = "Existing", KeyHash = "hash" });
        _db.SaveChanges();
        _actor.GetCurrentUserId().Returns(11);
        _tenant.IsSysAdmin().Returns(true);
        _service = new(_db, _actor, _tenant, Substitute.For<ITenantProvisioningService>(),
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, NullLogger<CompanyMembershipService>.Instance);
    }

    [Theory]
    [InlineData(EUserRole.User, true)]
    [InlineData(EUserRole.Admin, true)]
    [InlineData(EUserRole.SysAdmin, false)]
    public async Task BothPersistedRoleAndCurrentContextMustBeSysAdmin(EUserRole persistedRole, bool context)
    {
        _db.User.Single(u => u.Id == 11).Role = persistedRole;
        await _db.SaveChangesAsync();
        _tenant.IsSysAdmin().Returns(context);
        await Should.ThrowAsync<UnauthorizedAccessException>(() => _service.ListForUserAsync(10));
        await Should.ThrowAsync<UnauthorizedAccessException>(() => _service.UpdateForUserAsync(10, 21, Change()));
        _db.UserCompanyMembership.Single(m => m.CompanyId == 21).IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task InactiveAdministratorCannotReadOrWriteMemberships()
    {
        _db.User.Single(u => u.Id == 11).IsActive = false; await _db.SaveChangesAsync();
        await Should.ThrowAsync<UnauthorizedAccessException>(() => _service.ListForUserAsync(10));
        await Should.ThrowAsync<UnauthorizedAccessException>(() => _service.UpdateForUserAsync(10, 21, Change()));
    }

    [Fact]
    public async Task RevokeSecondaryPreservesDefaultCredentialsOtherMembershipsAndMachineGrants()
    {
        var result = await _service.UpdateForUserAsync(10, 21, Change());
        result.IsActive.ShouldBeFalse(); result.Role.ShouldBe(EUserRole.User);
        var user = _db.User.Single(u => u.Id == 10);
        user.IsActive.ShouldBeTrue(); user.CompanyId.ShouldBe(20); user.Role.ShouldBe(EUserRole.User);
        user.PasswordHash.ShouldBe("unchanged");
        _db.UserCompanyMembership.Single(m => m.CompanyId == 20).IsActive.ShouldBeTrue();
        _db.ApiKey.Single().AllowedCompanyIds.ShouldBe(new long[] { 20 });
        var all = await _service.ListForUserAsync(10);
        all.Count.ShouldBe(2); all.Single(m => m.CompanyId == 21).IsActive.ShouldBeFalse();
        _actor.GetCurrentUserId().Returns(10);
        (await _service.ListAsync()).Select(m => m.CompanyId).ShouldBe(new long[] { 20 });
    }

    [Fact]
    public async Task RevokeDefaultDoesNotRecreateItOrDisableSecondary()
    {
        await _service.UpdateForUserAsync(10, 20, Change());
        _db.User.Single(u => u.Id == 10).FirstName = "Profile edit"; await _db.SaveChangesAsync();
        _db.UserCompanyMembership.Single(m => m.CompanyId == 20).IsActive.ShouldBeFalse();
        _db.UserCompanyMembership.Single(m => m.CompanyId == 21).IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task RoleChangeAndRestoreNeverExpandCredentialGrants()
    {
        await _service.UpdateForUserAsync(10, 21, Change());
        var result = await _service.UpdateForUserAsync(10, 21, Change(true, EUserRole.Admin));
        result.IsActive.ShouldBeTrue(); result.Role.ShouldBe(EUserRole.Admin);
        _db.ApiKey.Single().AllowedCompanyIds.ShouldBe(new long[] { 20 });
    }

    [Theory]
    [InlineData(EUserRole.SysAdmin)]
    [InlineData((EUserRole)99)]
    public async Task RejectsPlatformAndUndefinedRoles(EUserRole role)
        => await Should.ThrowAsync<ValidationException>(() => _service.UpdateForUserAsync(10, 21, Change(true, role)));

    [Fact]
    public async Task MissingFieldsAndMissingMembershipCannotCreateOrSilentlyRevoke()
    {
        await Should.ThrowAsync<ValidationException>(() => _service.UpdateForUserAsync(10, 21, new()));
        await Should.ThrowAsync<KeyNotFoundException>(() => _service.UpdateForUserAsync(10, 999, Change()));
        await Should.ThrowAsync<KeyNotFoundException>(() => _service.ListForUserAsync(999));
        _db.UserCompanyMembership.Count().ShouldBe(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InactiveAccountOrCompanyCannotBeRestored(bool inactiveAccount)
    {
        if (inactiveAccount) _db.User.Single(u => u.Id == 10).IsActive = false;
        else _db.Client.Single(c => c.Id == 21).IsActive = false;
        await _db.SaveChangesAsync();
        await Should.ThrowAsync<ValidationException>(() => _service.UpdateForUserAsync(10, 21, Change(true)));
        (await _service.UpdateForUserAsync(10, 21, Change())).IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task OldInvitationCannotReopenRevokedMembership()
    {
        await _service.UpdateForUserAsync(10, 21, Change());
        var invite = await _service.InviteAsync(new() { Email = "member@test.invalid", CompanyId = 21, Role = EUserRole.Admin });
        await _service.UpdateForUserAsync(10, 21, Change());
        _actor.GetCurrentUserId().Returns(10);
        await Should.ThrowAsync<ValidationException>(() => _service.AcceptAsync(invite.Token!));
        _db.UserCompanyMembership.Single(m => m.CompanyId == 21).IsActive.ShouldBeFalse();
    }

    private static UpdateCompanyMembershipDto Change(bool active = false, EUserRole role = EUserRole.User) => new() { Role = role, IsActive = active };
    public void Dispose() => _db.Dispose();
}
