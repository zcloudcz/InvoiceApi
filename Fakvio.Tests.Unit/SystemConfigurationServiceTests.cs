using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.SystemConfiguration;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for SystemConfigurationService.
/// Tests the single-row CRUD pattern: auto-creation of defaults, reading, and updating.
///
/// Uses InMemoryDatabase for isolation — each test gets a fresh database.
/// SystemConfiguration is stored in MasterDbContext (not TenantDbContext).
/// </summary>
public class SystemConfigurationServiceTests : IDisposable
{
    private readonly MasterDbContext _context;
    private readonly SystemConfigurationService _service;

    public SystemConfigurationServiceTests()
    {
        // Setup in-memory database with a unique name for test isolation
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(databaseName: $"SystemConfigTest_{Guid.NewGuid()}")
            .Options;

        _context = new MasterDbContext(options);
        var logger = Substitute.For<ILogger<SystemConfigurationService>>();

        // Pass-through credential protector — no real encryption in unit tests
        var credentialProtector = Substitute.For<ICredentialProtector>();
        credentialProtector.Encrypt(Arg.Any<string?>()).Returns(ci => ci.Arg<string?>());
        credentialProtector.Decrypt(Arg.Any<string?>()).Returns(ci => ci.Arg<string?>());

        _service = new SystemConfigurationService(_context, credentialProtector, logger);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    /// <summary>
    /// When no SystemConfiguration row exists (fresh install),
    /// GetAsync should auto-create a default row and return it.
    /// </summary>
    [Fact]
    public async Task GetAsync_NoExistingRow_CreatesDefaultAndReturns()
    {
        // Act
        var result = await _service.GetAsync();

        // Assert — defaults should be returned
        result.ShouldNotBeNull();
        result.SmtpPort.ShouldBe(587); // Default SMTP port
        result.SmtpUseSsl.ShouldBeTrue();
        result.JwtExpirationHours.ShouldBe(24);
        result.SmtpHost.ShouldBe(""); // Empty until configured
        result.Id.ShouldBeGreaterThan(0); // Auto-generated ID

        // Verify the row was persisted to the database
        var count = await _context.Set<SystemConfiguration>().CountAsync();
        count.ShouldBe(1);
    }

    /// <summary>
    /// When a SystemConfiguration row already exists,
    /// GetAsync should return it without creating a new one.
    /// </summary>
    [Fact]
    public async Task GetAsync_ExistingRow_ReturnsExisting()
    {
        // Arrange — pre-seed a configuration row
        _context.Set<SystemConfiguration>().Add(new SystemConfiguration
        {
            SmtpHost = "smtp.example.com",
            SmtpPort = 465,
            SmtpSenderEmail = "admin@example.com",
            SmtpSenderName = "Example",
            SmtpUseSsl = true,
            JwtExpirationHours = 48,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetAsync();

        // Assert — should return the seeded values, not defaults
        result.SmtpHost.ShouldBe("smtp.example.com");
        result.SmtpPort.ShouldBe(465);
        result.SmtpSenderEmail.ShouldBe("admin@example.com");
        result.JwtExpirationHours.ShouldBe(48);

        // Should still be only one row
        var count = await _context.Set<SystemConfiguration>().CountAsync();
        count.ShouldBe(1);
    }

    /// <summary>
    /// UpdateAsync should modify the existing single row.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_UpdatesExistingRow()
    {
        // Arrange — create default row first
        await _service.GetAsync();

        var updateDto = new UpdateSystemConfigurationDto
        {
            SmtpHost = "smtp.updated.com",
            SmtpPort = 465,
            SmtpUsername = "user@updated.com",
            SmtpPassword = "secret123",
            SmtpSenderEmail = "noreply@updated.com",
            SmtpSenderName = "Updated App",
            SmtpUseSsl = false,
            JwtExpirationHours = 72
        };

        // Act
        var result = await _service.UpdateAsync(updateDto);

        // Assert
        result.SmtpHost.ShouldBe("smtp.updated.com");
        result.SmtpPort.ShouldBe(465);
        result.SmtpUsername.ShouldBe("user@updated.com");
        // SmtpPassword is no longer in the DTO (security: never sent to UI).
        // Verify via the dedicated internal method instead.
        result.HasSmtpPassword.ShouldBeTrue();
        result.SmtpSenderEmail.ShouldBe("noreply@updated.com");
        result.SmtpSenderName.ShouldBe("Updated App");
        result.SmtpUseSsl.ShouldBeFalse();
        result.JwtExpirationHours.ShouldBe(72);

        // Verify persisted to database
        var entity = await _context.Set<SystemConfiguration>().FirstAsync();
        entity.SmtpHost.ShouldBe("smtp.updated.com");
        entity.JwtExpirationHours.ShouldBe(72);
    }

    /// <summary>
    /// UpdateAsync on a fresh database should auto-create the row, then update it.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_NoExistingRow_CreatesAndUpdates()
    {
        // Arrange — no pre-existing row
        var updateDto = new UpdateSystemConfigurationDto
        {
            SmtpHost = "smtp.new.com",
            SmtpPort = 587,
            SmtpSenderEmail = "new@new.com",
            SmtpSenderName = "New App",
            SmtpUseSsl = true,
            JwtExpirationHours = 12
        };

        // Act
        var result = await _service.UpdateAsync(updateDto);

        // Assert
        result.SmtpHost.ShouldBe("smtp.new.com");
        result.JwtExpirationHours.ShouldBe(12);

        // Should have exactly one row
        var count = await _context.Set<SystemConfiguration>().CountAsync();
        count.ShouldBe(1);
    }

    /// <summary>
    /// Multiple calls to GetAsync should always return the same single row.
    /// </summary>
    [Fact]
    public async Task GetAsync_MultipleCalls_ReturnsSameRow()
    {
        // Act
        var result1 = await _service.GetAsync();
        var result2 = await _service.GetAsync();

        // Assert — same row ID, no duplicates
        result1.Id.ShouldBe(result2.Id);

        var count = await _context.Set<SystemConfiguration>().CountAsync();
        count.ShouldBe(1);
    }
}
