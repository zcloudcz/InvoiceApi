using InvoiceApi.Domain.Entities;
using InvoiceApi.Infrastructure.Data;
using InvoiceApi.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace InvoiceApi.Tests.Unit;

/// <summary>
/// Unit tests for TenantProvisioningService.
///
/// Tests the service's logic for managing tenant lifecycle:
/// - Activate/deactivate tenants (flips IsActive flag in CompanySystemSettings)
/// - Provisioning validation (throws for missing settings, already provisioned)
/// - MigrateAllTenantsAsync (queries active tenants)
///
/// Architecture: PostgreSQL multi-schema — all tenants share one database,
/// each tenant gets its own schema (e.g., "tenant_42").
///
/// Note: Full provisioning (CREATE SCHEMA, apply migrations, copy code tables)
/// cannot be tested with InMemoryDatabase — those are integration test scenarios.
/// We focus on the service's state management and validation logic here.
/// </summary>
public class TenantProvisioningServiceTests : IDisposable
{
    private readonly MasterDbContext _masterContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TenantProvisioningService> _logger;
    private readonly TenantProvisioningService _service;

    public TenantProvisioningServiceTests()
    {
        // In-memory MasterDbContext for tenant metadata management
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _masterContext = new MasterDbContext(options);

        // Configuration with a mock PostgreSQL connection string (won't actually connect in unit tests)
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=invoiceapi;Username=invoiceapi;Password=test"
            })
            .Build();

        _logger = Substitute.For<ILogger<TenantProvisioningService>>();
        _service = new TenantProvisioningService(_masterContext, _configuration, _logger);
    }

    /// <summary>
    /// Seeds a company (Client with IsIssuer = true) in the master database.
    /// Required for FK relationship with CompanySystemSettings.
    /// </summary>
    private async Task<Client> SeedCompanyAsync(long id = 1)
    {
        var client = new Client
        {
            Id = id,
            CompanyName = $"Test Company {id}",
            RegistrationNumber = $"REG{id:D8}",
            IsIssuer = true,
            IsActive = true,
            IsVatPayer = true,
            Address = new List<Address>(),
            Contact = new List<Contact>()
        };

        _masterContext.Client.Add(client);
        await _masterContext.SaveChangesAsync();
        return client;
    }

    /// <summary>
    /// Seeds CompanySystemSettings for a company.
    /// The company must be seeded first via SeedCompanyAsync.
    /// </summary>
    private async Task<CompanySystemSettings> SeedSettingsAsync(
        long companyId,
        bool isProvisioned = false,
        bool isActive = false)
    {
        var settings = new CompanySystemSettings
        {
            CompanyId = companyId,
            SchemaName = $"tenant_{companyId}",
            IsProvisioned = isProvisioned,
            IsActive = isActive,
            ProvisionedAt = isProvisioned ? DateTime.UtcNow : null
        };

        _masterContext.CompanySystemSettings.Add(settings);
        await _masterContext.SaveChangesAsync();
        return settings;
    }

    #region ActivateTenantAsync Tests

    [Fact]
    public async Task ActivateTenantAsync_ProvisionedTenant_SetsIsActiveTrue()
    {
        // Arrange — company with provisioned but inactive tenant
        await SeedCompanyAsync(1);
        await SeedSettingsAsync(1, isProvisioned: true, isActive: false);

        // Act
        var result = await _service.ActivateTenantAsync(1);

        // Assert
        result.ShouldBeTrue();
        var settings = await _masterContext.CompanySystemSettings
            .FirstOrDefaultAsync(s => s.CompanyId == 1);
        settings!.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task ActivateTenantAsync_NotFound_ReturnsFalse()
    {
        // Arrange — no settings exist for company 999

        // Act
        var result = await _service.ActivateTenantAsync(999);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task ActivateTenantAsync_NotProvisioned_ThrowsInvalidOperationException()
    {
        // Arrange — company with settings but NOT provisioned
        await SeedCompanyAsync(2);
        await SeedSettingsAsync(2, isProvisioned: false, isActive: false);

        // Act & Assert — can't activate what hasn't been provisioned
        var act = () => _service.ActivateTenantAsync(2);
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("not provisioned");
    }

    #endregion

    #region DeactivateTenantAsync Tests

    [Fact]
    public async Task DeactivateTenantAsync_ActiveTenant_SetsIsActiveFalse()
    {
        // Arrange — company with active tenant
        await SeedCompanyAsync(3);
        await SeedSettingsAsync(3, isProvisioned: true, isActive: true);

        // Act
        var result = await _service.DeactivateTenantAsync(3);

        // Assert
        result.ShouldBeTrue();
        var settings = await _masterContext.CompanySystemSettings
            .FirstOrDefaultAsync(s => s.CompanyId == 3);
        settings!.IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task DeactivateTenantAsync_NotFound_ReturnsFalse()
    {
        // Act
        var result = await _service.DeactivateTenantAsync(999);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task DeactivateTenantAsync_AlreadyInactive_StillReturnsTrue()
    {
        // Arrange — already inactive tenant (idempotent operation)
        await SeedCompanyAsync(4);
        await SeedSettingsAsync(4, isProvisioned: true, isActive: false);

        // Act
        var result = await _service.DeactivateTenantAsync(4);

        // Assert — should succeed (idempotent)
        result.ShouldBeTrue();
    }

    #endregion

    #region ProvisionTenantAsync Validation Tests

    [Fact]
    public async Task ProvisionTenantAsync_NoSettings_ThrowsInvalidOperationException()
    {
        // Arrange — company exists but no CompanySystemSettings
        await SeedCompanyAsync(5);

        // Act & Assert — can't provision without settings
        var act = () => _service.ProvisionTenantAsync(5);
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("CompanySystemSettings not found");
    }

    /// <summary>
    /// Verifies that re-provisioning an already provisioned company does NOT throw.
    /// The provisioning flow is idempotent — safe to re-run after partial failure.
    /// It clears and re-seeds code tables, checks for existing issuer, etc.
    /// NOTE: This test will still fail at the CREATE SCHEMA step because
    /// there's no real PostgreSQL available in unit tests. The important thing is
    /// that it does NOT throw InvalidOperationException for "already provisioned".
    /// </summary>
    [Fact]
    public async Task ProvisionTenantAsync_AlreadyProvisioned_DoesNotThrowAlreadyProvisionedException()
    {
        // Arrange — company already provisioned
        await SeedCompanyAsync(6);
        await SeedSettingsAsync(6, isProvisioned: true, isActive: true);

        // Act — re-provisioning should NOT throw "already provisioned" exception.
        // It WILL throw because there's no real PostgreSQL for CREATE SCHEMA,
        // but the error should be about the database connection, not about "already provisioned".
        var act = () => _service.ProvisionTenantAsync(6);
        var ex = await Should.ThrowAsync<Exception>(act);
        ex.Message.ShouldNotContain("already provisioned");
    }

    #endregion

    #region MigrateAllTenantsAsync Tests

    [Fact]
    public async Task MigrateAllTenantsAsync_NoTenants_ReturnsZero()
    {
        // Arrange — no tenants in the database

        // Act
        var result = await _service.MigrateAllTenantsAsync();

        // Assert
        result.ShouldBe(0);
    }

    [Fact]
    public async Task MigrateAllTenantsAsync_OnlyInactiveTenants_ReturnsZero()
    {
        // Arrange — only inactive tenants exist (should not be migrated)
        await SeedCompanyAsync(7);
        await SeedSettingsAsync(7, isProvisioned: true, isActive: false);

        // Act — MigrateAllTenantsAsync only processes provisioned + active tenants
        // Since we use InMemoryDb, the actual migration will fail, but it filters correctly
        // The method catches per-tenant exceptions, so inactive ones are skipped entirely
        var result = await _service.MigrateAllTenantsAsync();

        // Assert — no active tenants to migrate
        result.ShouldBe(0);
    }

    [Fact]
    public async Task MigrateAllTenantsAsync_UnprovisionedTenants_ReturnsZero()
    {
        // Arrange — only unprovisioned tenants (should not be migrated)
        await SeedCompanyAsync(8);
        await SeedSettingsAsync(8, isProvisioned: false, isActive: true);

        // Act
        var result = await _service.MigrateAllTenantsAsync();

        // Assert — unprovisioned tenants are skipped
        result.ShouldBe(0);
    }

    #endregion

    #region MigrateTenantAsync Tests

    [Fact]
    public async Task MigrateTenantAsync_NotFound_ReturnsFalse()
    {
        // Act — no settings for company 999
        var result = await _service.MigrateTenantAsync(999);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task MigrateTenantAsync_NotProvisioned_ReturnsFalse()
    {
        // Arrange — settings exist but not provisioned
        await SeedCompanyAsync(9);
        await SeedSettingsAsync(9, isProvisioned: false);

        // Act
        var result = await _service.MigrateTenantAsync(9);

        // Assert — can't migrate what doesn't exist
        result.ShouldBeFalse();
    }

    #endregion

    public void Dispose()
    {
        _masterContext.Database.EnsureDeleted();
        _masterContext.Dispose();
    }
}
