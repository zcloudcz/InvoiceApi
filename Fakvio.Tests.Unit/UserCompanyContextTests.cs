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

public class UserCompanyContextTests : IDisposable
{
    private readonly MasterDbContext _db = new(new DbContextOptionsBuilder<MasterDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly UserService _service;
    public UserCompanyContextTests()
    {
        _db.Client.AddRange(new Client { Id = 20, IsIssuer = true, RegistrationNumber = "20", CompanyName = "Default" }, new Client { Id = 21, IsIssuer = true, RegistrationNumber = "21", CompanyName = "Second" });
        _db.User.Add(new User { Id = 10, CompanyId = 20, Email = "member@test.cz", Role = EUserRole.Admin });
        _db.UserCompanyMembership.Add(new UserCompanyMembership { UserId = 10, CompanyId = 21, Role = EUserRole.User });
        _db.SaveChanges();
        _service = new(_db, Substitute.For<IAuthService>(), Substitute.For<ITenantProvisioningService>(), Substitute.For<IOAuthService>(), NullLogger<UserService>.Instance);
    }
    [Fact]
    public async Task CompanyFilteredLists_FindSecondaryMembershipWithoutChangingEditableDefaultIdentity()
    {
        var users = await _service.GetAllUsersAsync(21);
        users.Single().Role.ShouldBe(EUserRole.Admin);
        users.Single().CompanyName.ShouldBe("Default");
        var page = await _service.GetUsersPagedAsync(new UserFilterDto { CompanyId = 21, Role = EUserRole.User });
        page.Items.Single().CompanyId.ShouldBe(20);
        (await _service.GetUsersPagedAsync(new UserFilterDto { CompanyId = 21, Role = EUserRole.Admin })).Items.ShouldBeEmpty();
    }
    [Fact]
    public async Task DefaultRoleEdit_PreservesSecondaryMembershipRole()
    {
        _db.UserCompanyMembership.Single(m => m.CompanyId == 21).Role = EUserRole.Admin;
        await _db.SaveChangesAsync();
        await _service.UpdateUserAsync(10, new UpdateUserDto { Role = EUserRole.User });
        _db.UserCompanyMembership.Single(m => m.CompanyId == 20).Role.ShouldBe(EUserRole.User);
        _db.UserCompanyMembership.Single(m => m.CompanyId == 21).Role.ShouldBe(EUserRole.Admin);
    }
    public void Dispose() => _db.Dispose();
}
