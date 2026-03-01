using Shouldly;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for the audit trail auto-fill mechanism in MasterDbContext.
/// Verifies that CreatedByUserId and UpdatedByUserId are automatically populated
/// from ICurrentUserService when entities are saved.
/// Also tests the NumberSequence RowVersion concurrency token property.
/// </summary>
public class AuditTrailTests : IDisposable
{
    private readonly MasterDbContext _context;
    private readonly ICurrentUserService _currentUserService;

    public AuditTrailTests()
    {
        // Setup in-memory database with a unique name for test isolation
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        // Substitute ICurrentUserService to simulate an authenticated user
        _currentUserService = Substitute.For<ICurrentUserService>();

        // Use the constructor that accepts ICurrentUserService
        _context = new MasterDbContext(options, _currentUserService);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    // ==================== Audit Trail - Create ====================

    [Fact]
    public async Task SaveChangesAsync_NewEntity_SetsCreatedByUserId()
    {
        // Arrange — simulate an authenticated user with ID 42
        _currentUserService.GetCurrentUserId().Returns(42L);

        var currency = new Currency
        {
            Code = "TST",
            Name = "Test Currency",
            Symbol = "T",
            IsActive = true
        };

        _context.Currency.Add(currency);

        // Act
        await _context.SaveChangesAsync();

        // Assert — CreatedByUserId should be auto-filled from the current user
        currency.CreatedByUserId.ShouldBe(42);
        Math.Abs((currency.CreatedAt - DateTime.UtcNow).TotalSeconds).ShouldBeLessThanOrEqualTo(5);
    }

    [Fact]
    public async Task SaveChangesAsync_NewEntity_SetsUpdatedByUserId()
    {
        // Arrange — simulate an authenticated user with ID 99
        _currentUserService.GetCurrentUserId().Returns(99L);

        var currency = new Currency
        {
            Code = "UP1",
            Name = "Update Test",
            Symbol = "U",
            IsActive = true
        };

        _context.Currency.Add(currency);

        // Act
        await _context.SaveChangesAsync();

        // Assert — UpdatedByUserId should also be set on create (first update = creation)
        currency.UpdatedByUserId.ShouldBe(99);
        currency.UpdatedAt.ShouldNotBeNull();
    }

    // ==================== Audit Trail - Update ====================

    [Fact]
    public async Task SaveChangesAsync_UpdatedEntity_SetsUpdatedByUserId()
    {
        // Arrange — create entity as user 10
        _currentUserService.GetCurrentUserId().Returns(10L);

        var currency = new Currency
        {
            Code = "MOD",
            Name = "Modified Currency",
            Symbol = "M",
            IsActive = true
        };

        _context.Currency.Add(currency);
        await _context.SaveChangesAsync();

        // Now switch to a different user (user 20) and update the entity
        _currentUserService.GetCurrentUserId().Returns(20L);

        currency.Name = "Updated Name";

        // Act
        await _context.SaveChangesAsync();

        // Assert — CreatedByUserId should remain 10, UpdatedByUserId should now be 20
        currency.CreatedByUserId.ShouldBe(10);
        currency.UpdatedByUserId.ShouldBe(20);
    }

    // ==================== Audit Trail - No User (anonymous) ====================

    [Fact]
    public async Task SaveChangesAsync_NoAuthenticatedUser_LeavesAuditFieldsNull()
    {
        // Arrange — simulate no authenticated user (anonymous request or migration)
        _currentUserService.GetCurrentUserId().Returns((long?)null);

        var currency = new Currency
        {
            Code = "ANO",
            Name = "Anonymous Currency",
            Symbol = "A",
            IsActive = true
        };

        _context.Currency.Add(currency);

        // Act
        await _context.SaveChangesAsync();

        // Assert — audit user IDs should remain null when no user is authenticated
        currency.CreatedByUserId.ShouldBeNull();
        currency.UpdatedByUserId.ShouldBeNull();
        // Timestamps should still be set regardless of authentication status
        Math.Abs((currency.CreatedAt - DateTime.UtcNow).TotalSeconds).ShouldBeLessThanOrEqualTo(5);
    }

    // ==================== Audit Trail - No ICurrentUserService ====================

    [Fact]
    public async Task SaveChangesAsync_WithoutCurrentUserService_StillWorks()
    {
        // Arrange — use the constructor that does NOT accept ICurrentUserService
        // (simulates migrations, background jobs, or console tooling)
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var contextWithoutService = new MasterDbContext(options);

        var currency = new Currency
        {
            Code = "NCS",
            Name = "No Current Service",
            Symbol = "N",
            IsActive = true
        };

        contextWithoutService.Currency.Add(currency);

        // Act — should not throw even without ICurrentUserService
        await contextWithoutService.SaveChangesAsync();

        // Assert — timestamps should still work, audit IDs should be null
        Math.Abs((currency.CreatedAt - DateTime.UtcNow).TotalSeconds).ShouldBeLessThanOrEqualTo(5);
        currency.CreatedByUserId.ShouldBeNull();
        currency.UpdatedByUserId.ShouldBeNull();

        contextWithoutService.Database.EnsureDeleted();
    }

    // ==================== NumberSequence RowVersion ====================

    [Fact]
    public void NumberSequence_RowVersionConcurrencyToken_PropertyExists()
    {
        // Arrange & Act — create a NumberSequence with RowVersion concurrency token.
        // SQL Server uses a rowversion (timestamp) column for optimistic concurrency.
        // The byte[] is auto-generated by the database — defaults to null before first save.
        var sequence = new NumberSequence
        {
            Id = 1,
            Name = "Test Seq",
            DocumentType = EDocumentType.Invoice,
            CurrentNumber = 0,
            IsDefault = true,
            IsActive = true,
            NumberSequenceFormatId = 0
        };

        // Assert — RowVersion property should exist and default to 0 (uint).
        // PostgreSQL xmin system column populates this automatically on INSERT/UPDATE.
        sequence.RowVersion.ShouldBe(0u);
    }
}
