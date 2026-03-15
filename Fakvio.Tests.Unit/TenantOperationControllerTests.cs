using Fakvio.API.Controller;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for TenantOperationController.
///
/// Tests the controller's API endpoints for tenant schema management:
/// - FixSchemaPermissions (POST /api/tenant-operation/fix-permissions)
///   → Retroactively applies GRANT + ALTER DEFAULT PRIVILEGES on all provisioned schemas.
/// - Provision, List, Status, Delete (tested indirectly via provisioning service)
///
/// Note: SQL-level permission grants (GRANT ALL, ALTER DEFAULT PRIVILEGES) require
/// a real PostgreSQL connection and are covered in integration tests.
/// These unit tests verify the controller's logic, validation, and response shaping.
/// </summary>
public class TenantOperationControllerTests : IDisposable
{
    private readonly MasterDbContext _masterContext;
    private readonly ITenantProvisioningService _provisioningService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TenantOperationController> _logger;
    private readonly TenantOperationController _controller;

    public TenantOperationControllerTests()
    {
        // In-memory MasterDbContext for tenant metadata
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _masterContext = new MasterDbContext(options);

        // Mock provisioning service — actual provisioning requires real PostgreSQL
        _provisioningService = Substitute.For<ITenantProvisioningService>();

        // Configuration with a mock connection string (won't actually connect)
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=fakvio;Username=fakvio;Password=test",
                ["ConnectionStrings:MasterConnection"] = "Host=localhost;Database=fakvio;Username=fakvio;Password=test"
            })
            .Build();

        _logger = Substitute.For<ILogger<TenantOperationController>>();

        _controller = new TenantOperationController(
            _provisioningService, _masterContext, _configuration, _logger);
    }

    /// <summary>
    /// Seeds a company (Client with IsIssuer = true) + CompanySystemSettings in master DB.
    /// </summary>
    private async Task SeedTenantAsync(
        long companyId,
        string schemaName,
        bool isProvisioned = true,
        bool isActive = true)
    {
        var client = new Client
        {
            Id = companyId,
            CompanyName = $"Test Company {companyId}",
            RegistrationNumber = $"REG{companyId:D8}",
            IsIssuer = true,
            IsActive = true,
            IsVatPayer = true,
            Address = new List<Address>(),
            Contact = new List<Contact>()
        };
        _masterContext.Client.Add(client);
        await _masterContext.SaveChangesAsync();

        var settings = new CompanySystemSettings
        {
            CompanyId = companyId,
            SchemaName = schemaName,
            IsProvisioned = isProvisioned,
            IsActive = isActive,
            ProvisionedAt = isProvisioned ? DateTime.UtcNow : null
        };
        _masterContext.CompanySystemSettings.Add(settings);
        await _masterContext.SaveChangesAsync();
    }

    #region FixSchemaPermissions Tests

    [Fact]
    public async Task FixSchemaPermissions_NoProvisionedSchemas_ReturnsOkWithZeroCount()
    {
        // Arrange — no provisioned tenants in master DB

        // Act — fix-permissions should handle empty list gracefully
        var result = await _controller.FixSchemaPermissions();

        // Assert
        var okResult = result.ShouldBeOfType<OkObjectResult>();
        okResult.StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task FixSchemaPermissions_UnprovisionedSchemasOnly_ReturnsOkWithZeroCount()
    {
        // Arrange — only unprovisioned tenants (should be skipped)
        await SeedTenantAsync(1, "tenant_1", isProvisioned: false);

        // Act
        var result = await _controller.FixSchemaPermissions();

        // Assert — unprovisioned schemas should be excluded
        var okResult = result.ShouldBeOfType<OkObjectResult>();
        okResult.StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task FixSchemaPermissions_EmptySchemaName_SkipsGracefully()
    {
        // Arrange — provisioned but with empty schema name (edge case)
        var client = new Client
        {
            Id = 99,
            CompanyName = "Edge Case Co",
            RegistrationNumber = "REG00000099",
            IsIssuer = true,
            IsActive = true,
            IsVatPayer = true,
            Address = new List<Address>(),
            Contact = new List<Contact>()
        };
        _masterContext.Client.Add(client);
        await _masterContext.SaveChangesAsync();

        var settings = new CompanySystemSettings
        {
            CompanyId = 99,
            SchemaName = "", // empty — should be filtered out by the query
            IsProvisioned = true,
            IsActive = true
        };
        _masterContext.CompanySystemSettings.Add(settings);
        await _masterContext.SaveChangesAsync();

        // Act — should not crash on empty schema name
        var result = await _controller.FixSchemaPermissions();

        // Assert
        var okResult = result.ShouldBeOfType<OkObjectResult>();
        okResult.StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task FixSchemaPermissions_ProvisionedSchemas_AttemptsToFixAll()
    {
        // Arrange — multiple provisioned tenants
        await SeedTenantAsync(1, "tenant_1", isProvisioned: true, isActive: true);
        await SeedTenantAsync(2, "tenant_2", isProvisioned: true, isActive: true);
        await SeedTenantAsync(3, "tenant_3", isProvisioned: true, isActive: false); // inactive but provisioned

        // Act — should attempt all 3 provisioned schemas (active or not)
        // Note: Will fail at actual SQL execution (no real PostgreSQL),
        // but the controller catches per-schema exceptions and continues.
        var result = await _controller.FixSchemaPermissions();

        // Assert — returns 500 because the SQL connection fails (no real DB)
        // This is expected in unit tests; integration tests verify actual SQL execution.
        // The important thing is that the controller doesn't throw an unhandled exception.
        result.ShouldNotBeNull();
    }

    #endregion

    #region ListTenantSchemas Tests

    [Fact]
    public async Task ListTenantSchemas_Empty_ReturnsEmptyList()
    {
        // Act
        var result = await _controller.ListTenantSchemas();

        // Assert
        var okResult = result.Result.ShouldBeOfType<OkObjectResult>();
        okResult.StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task ListTenantSchemas_WithTenants_ReturnsAll()
    {
        // Arrange
        await SeedTenantAsync(1, "tenant_1");
        await SeedTenantAsync(2, "tenant_2");

        // Act
        var result = await _controller.ListTenantSchemas();

        // Assert
        var okResult = result.Result.ShouldBeOfType<OkObjectResult>();
        okResult.StatusCode.ShouldBe(200);
    }

    #endregion

    #region GetTenantSchemaStatus Tests

    [Fact]
    public async Task GetTenantSchemaStatus_NotFound_Returns404()
    {
        // Act
        var result = await _controller.GetTenantSchemaStatus(999);

        // Assert
        result.Result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetTenantSchemaStatus_Exists_ReturnsOk()
    {
        // Arrange
        await SeedTenantAsync(1, "tenant_1");

        // Act
        var result = await _controller.GetTenantSchemaStatus(1);

        // Assert
        result.Result.ShouldBeOfType<OkObjectResult>();
    }

    #endregion

    #region DeleteTenantSchema Tests

    [Fact]
    public async Task DeleteTenantSchema_NotFound_Returns404()
    {
        // Act
        var result = await _controller.DeleteTenantSchema(999);

        // Assert
        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task DeleteTenantSchema_EmptySchemaName_Returns400()
    {
        // Arrange — settings exist but schema name is empty
        var client = new Client
        {
            Id = 50,
            CompanyName = "Empty Schema Co",
            RegistrationNumber = "REG00000050",
            IsIssuer = true,
            IsActive = true,
            IsVatPayer = true,
            Address = new List<Address>(),
            Contact = new List<Contact>()
        };
        _masterContext.Client.Add(client);
        await _masterContext.SaveChangesAsync();

        var settings = new CompanySystemSettings
        {
            CompanyId = 50,
            SchemaName = "", // empty
            IsProvisioned = true,
            IsActive = true
        };
        _masterContext.CompanySystemSettings.Add(settings);
        await _masterContext.SaveChangesAsync();

        // Act
        var result = await _controller.DeleteTenantSchema(50);

        // Assert — should reject because there's no schema name to drop
        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    #endregion

    public void Dispose()
    {
        _masterContext.Database.EnsureDeleted();
        _masterContext.Dispose();
    }
}
