using Fakvio.Contracts.Dto.User;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for UserPreferencesService — lazy upsert semantics,
/// defaults for users without a row, and page-size validation.
/// Uses InMemoryDatabase (master DB context).
/// </summary>
public class UserPreferencesServiceTests : IDisposable
{
    private readonly MasterDbContext _context;
    private readonly UserPreferencesService _service;

    public UserPreferencesServiceTests()
    {
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new MasterDbContext(options);
        _service = new UserPreferencesService(_context, Substitute.For<ILogger<UserPreferencesService>>());

        // FK target — preferences row references a user
        _context.User.Add(new User
        {
            Id = 1, Email = "user@test.cz", FirstName = "Test", LastName = "User",
            Role = EUserRole.User, IsActive = true, IsEmailVerified = true,
            ExternalProvider = EExternalProvider.None
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Fact]
    public async Task GetAsync_NoRowSaved_ReturnsDefaults()
    {
        var result = await _service.GetAsync(1);

        result.DefaultGridPageSize.ShouldBe(10);
        // Reading must NOT create a row — lazy upsert happens only on save
        (await _context.UserPreferences.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task UpdateAsync_FirstSave_CreatesRow()
    {
        var result = await _service.UpdateAsync(1, new UserPreferencesDto { DefaultGridPageSize = 50 });

        result.DefaultGridPageSize.ShouldBe(50);
        var row = await _context.UserPreferences.SingleAsync();
        row.UserId.ShouldBe(1);
        row.DefaultGridPageSize.ShouldBe(50);
    }

    [Fact]
    public async Task UpdateAsync_SecondSave_UpdatesExistingRow()
    {
        await _service.UpdateAsync(1, new UserPreferencesDto { DefaultGridPageSize = 25 });
        await _service.UpdateAsync(1, new UserPreferencesDto { DefaultGridPageSize = 100 });

        // Still one row (upsert), with the latest value
        (await _context.UserPreferences.CountAsync()).ShouldBe(1);
        (await _service.GetAsync(1)).DefaultGridPageSize.ShouldBe(100);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(-10)]
    [InlineData(1000)]
    public async Task UpdateAsync_InvalidPageSize_Throws(int pageSize)
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _service.UpdateAsync(1, new UserPreferencesDto { DefaultGridPageSize = pageSize }));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(100)]
    public async Task UpdateAsync_AllowedPageSizes_Succeed(int pageSize)
    {
        var result = await _service.UpdateAsync(1, new UserPreferencesDto { DefaultGridPageSize = pageSize });
        result.DefaultGridPageSize.ShouldBe(pageSize);
    }

    [Fact]
    public async Task GetAsync_DifferentUser_DoesNotSeeOthersPreferences()
    {
        _context.User.Add(new User
        {
            Id = 2, Email = "other@test.cz", FirstName = "Other", LastName = "User",
            Role = EUserRole.User, IsActive = true, IsEmailVerified = true,
            ExternalProvider = EExternalProvider.None
        });
        _context.SaveChanges();

        await _service.UpdateAsync(1, new UserPreferencesDto { DefaultGridPageSize = 100 });

        (await _service.GetAsync(2)).DefaultGridPageSize.ShouldBe(10);
    }
}
