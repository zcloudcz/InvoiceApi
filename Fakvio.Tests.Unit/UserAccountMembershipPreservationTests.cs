using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.User;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>Account profile edits must not undo the separate membership administration flow.</summary>
public sealed class UserAccountMembershipPreservationTests : IDisposable
{
    private readonly MasterDbContext _db = new(new DbContextOptionsBuilder<MasterDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly UserService _service;
    public UserAccountMembershipPreservationTests()
    {
        _db.Client.AddRange(new Client { Id = 20, CompanyName = "A", IsIssuer = true, RegistrationNumber = "12345678" },
            new Client { Id = 21, CompanyName = "B", IsIssuer = true, RegistrationNumber = "87654321" });
        _db.User.Add(new User { Id = 10, CompanyId = 20, Role = EUserRole.Admin, Email = "accountant@example.test" });
        _db.SaveChanges();
        _service = new UserService(_db, Substitute.For<IAuthService>(), Substitute.For<ITenantProvisioningService>(),
            Substitute.For<IOAuthService>(), NullLogger<UserService>.Instance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProfileSave_PreservesRevocationAndIndependentRole(bool active)
    {
        var membership = _db.UserCompanyMembership.Single();
        membership.Role = EUserRole.User;
        membership.IsActive = active;
        await _db.SaveChangesAsync();
        await _service.UpdateUserAsync(10, new UpdateUserDto { CompanyId = 20, Role = EUserRole.Admin, FirstName = "Renamed" });
        membership.IsActive.ShouldBe(active);
        membership.Role.ShouldBe(EUserRole.User);
        _db.User.Single().FirstName.ShouldBe("Renamed");
    }

    [Fact]
    public async Task SelectingRevokedDefault_RequiresExplicitMembershipRestore()
    {
        _db.UserCompanyMembership.Add(new UserCompanyMembership { UserId = 10, CompanyId = 21, Role = EUserRole.User, IsActive = false });
        await _db.SaveChangesAsync();
        var error = await Should.ThrowAsync<InvalidOperationException>(() => _service.UpdateUserAsync(10,
            new UpdateUserDto { CompanyId = 21, Role = EUserRole.Admin }));
        error.Message.ShouldBe(UserErrorCodes.CompanyMembershipRestoreRequired);
        _db.ChangeTracker.Clear();
        _db.User.Single().CompanyId.ShouldBe(20);
        _db.UserCompanyMembership.Single(m => m.CompanyId == 21).IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task SelectingActiveDefault_DoesNotReplaceItsCompanyRole()
    {
        _db.UserCompanyMembership.Add(new UserCompanyMembership { UserId = 10, CompanyId = 21, Role = EUserRole.User });
        await _db.SaveChangesAsync();
        await _service.UpdateUserAsync(10, new UpdateUserDto { CompanyId = 21, Role = EUserRole.Admin });
        _db.UserCompanyMembership.Single(m => m.CompanyId == 21).Role.ShouldBe(EUserRole.User);
        _db.User.Single().Role.ShouldBe(EUserRole.User);
    }

    [Fact]
    public async Task SelectingNewDefault_CreatesOnlyTheNewMembership()
    {
        await _service.UpdateUserAsync(10, new UpdateUserDto { CompanyId = 21, Role = EUserRole.Admin });
        _db.UserCompanyMembership.Count().ShouldBe(2);
        _db.UserCompanyMembership.Single(m => m.CompanyId == 21).Role.ShouldBe(EUserRole.Admin);
        _db.UserCompanyMembership.All(m => m.IsActive).ShouldBeTrue();
    }

    public void Dispose() => _db.Dispose();
}
