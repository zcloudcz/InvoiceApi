using InvoiceApi.Application.Service;
using InvoiceApi.Contracts.Dto.AzureOperation;
using InvoiceApi.Domain.Entities;
using InvoiceApi.Infrastructure.Data;
using InvoiceApi.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using System.ComponentModel.DataAnnotations;

namespace InvoiceApi.Tests.Unit;

/// <summary>
/// Unit tests for the AzureOperation feature:
/// - AzureCreateDatabaseRequest DTO validation (required fields, regex patterns, ranges)
/// - AzureSqlService identity name sanitization (SQL injection prevention)
/// - AzureOperationController provisioning flow logic (with mocked IAzureSqlService)
///
/// Note: Actual Azure ARM API calls (CreateDatabase, ListDatabases, etc.) cannot be tested
/// in unit tests — they require a real Azure subscription. Those are integration test scenarios.
/// We focus on validation, sanitization, and controller orchestration logic here.
/// </summary>
public class AzureSqlServiceTests : IDisposable
{
    private readonly MasterDbContext _masterContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AzureSqlService> _logger;

    public AzureSqlServiceTests()
    {
        // In-memory MasterDbContext for controller tests that read CompanySystemSettings
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _masterContext = new MasterDbContext(options);

        // Configuration with Azure settings (won't connect to real Azure in unit tests)
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureSettings:SubscriptionId"] = "00000000-0000-0000-0000-000000000000",
                ["AzureSettings:ResourceGroupName"] = "test-rg",
                ["AzureSettings:SqlServerName"] = "test-sql-server",
                ["AzureSettings:Location"] = "westeurope",
                ["AzureSettings:FunctionAppIdentityName"] = "my-func-app",
                ["ConnectionStrings:MasterConnection"] = "Server=localhost;Database=master;User Id=test;Password=test;TrustServerCertificate=true"
            })
            .Build();

        _logger = Substitute.For<ILogger<AzureSqlService>>();
    }

    /// <summary>
    /// Seeds a company (Client with IsIssuer = true) in the in-memory master database.
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
            DatabaseName = $"invoiceapi_tenant_{companyId}",
            IsProvisioned = isProvisioned,
            IsActive = isActive,
            ProvisionedAt = isProvisioned ? DateTime.UtcNow : null
        };

        _masterContext.CompanySystemSettings.Add(settings);
        await _masterContext.SaveChangesAsync();
        return settings;
    }

    #region DTO Validation Tests

    /// <summary>
    /// Helper to run DataAnnotations validation on a DTO.
    /// Returns the list of validation errors (empty if valid).
    /// </summary>
    private static List<ValidationResult> ValidateDto(object dto)
    {
        var results = new List<ValidationResult>();
        var context = new ValidationContext(dto);
        Validator.TryValidateObject(dto, context, results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void AzureCreateDatabaseRequest_ValidRequest_PassesValidation()
    {
        // Arrange — a fully valid request with all defaults
        var request = new AzureCreateDatabaseRequest
        {
            DatabaseName = "invoiceapi_tenant_42",
            CompanyId = 42
        };

        // Act
        var errors = ValidateDto(request);

        // Assert — no validation errors
        errors.ShouldBeEmpty();
    }

    [Fact]
    public void AzureCreateDatabaseRequest_EmptyDatabaseName_FailsValidation()
    {
        // Arrange — missing database name
        var request = new AzureCreateDatabaseRequest
        {
            DatabaseName = "",
            CompanyId = 1
        };

        // Act
        var errors = ValidateDto(request);

        // Assert — should have validation error for DatabaseName
        errors.ShouldNotBeEmpty();
        errors.ShouldContain(e => e.MemberNames.Contains("DatabaseName"));
    }

    [Fact]
    public void AzureCreateDatabaseRequest_InvalidDatabaseNameCharacters_FailsValidation()
    {
        // Arrange — database name with invalid characters (spaces, special chars)
        var request = new AzureCreateDatabaseRequest
        {
            DatabaseName = "my database; DROP TABLE users--",
            CompanyId = 1
        };

        // Act
        var errors = ValidateDto(request);

        // Assert — regex validation should reject this
        errors.ShouldNotBeEmpty();
        errors.ShouldContain(e => e.MemberNames.Contains("DatabaseName"));
    }

    [Fact]
    public void AzureCreateDatabaseRequest_ZeroCompanyId_FailsValidation()
    {
        // Arrange — CompanyId must be >= 1
        var request = new AzureCreateDatabaseRequest
        {
            DatabaseName = "valid_name",
            CompanyId = 0
        };

        // Act
        var errors = ValidateDto(request);

        // Assert
        errors.ShouldNotBeEmpty();
        errors.ShouldContain(e => e.MemberNames.Contains("CompanyId"));
    }

    [Fact]
    public void AzureCreateDatabaseRequest_InvalidFreeLimitBehavior_FailsValidation()
    {
        // Arrange — invalid exhaustion behavior value
        var request = new AzureCreateDatabaseRequest
        {
            DatabaseName = "valid_name",
            CompanyId = 1,
            FreeLimitExhaustionBehavior = "InvalidValue"
        };

        // Act
        var errors = ValidateDto(request);

        // Assert — regex should reject anything other than AutoPause or BillOverUsage
        errors.ShouldNotBeEmpty();
        errors.ShouldContain(e => e.MemberNames.Contains("FreeLimitExhaustionBehavior"));
    }

    [Fact]
    public void AzureCreateDatabaseRequest_ValidBillOverUsage_PassesValidation()
    {
        // Arrange — BillOverUsage is a valid value
        var request = new AzureCreateDatabaseRequest
        {
            DatabaseName = "tenant_db",
            CompanyId = 1,
            FreeLimitExhaustionBehavior = "BillOverUsage"
        };

        // Act
        var errors = ValidateDto(request);

        // Assert
        errors.ShouldBeEmpty();
    }

    [Fact]
    public void AzureCreateDatabaseRequest_DefaultValues_AreCorrect()
    {
        // Arrange & Act — check default values
        var request = new AzureCreateDatabaseRequest();

        // Assert — verify all defaults match the plan
        request.UseFreeOffer.ShouldBeTrue();
        request.FreeLimitExhaustionBehavior.ShouldBe("AutoPause");
        request.MaxSizeBytes.ShouldBe(34359738368); // 32 GB
    }

    [Fact]
    public void AzureCreateDatabaseRequest_DatabaseNameWithUnderscoresAndNumbers_PassesValidation()
    {
        // Arrange — valid name with underscores and numbers (common pattern)
        var request = new AzureCreateDatabaseRequest
        {
            DatabaseName = "invoiceapi_tenant_123_test",
            CompanyId = 1
        };

        // Act
        var errors = ValidateDto(request);

        // Assert
        errors.ShouldBeEmpty();
    }

    #endregion

    #region AzureDatabaseStatusDto Tests

    [Fact]
    public void AzureDatabaseStatusDto_DefaultValues_AreCorrect()
    {
        // Arrange & Act — check default values
        var dto = new AzureDatabaseStatusDto();

        // Assert — all string defaults should be empty, booleans false, nullables null
        dto.DatabaseName.ShouldBe(string.Empty);
        dto.Status.ShouldBe(string.Empty);
        dto.CreationDate.ShouldBeNull();
        dto.MaxSizeBytes.ShouldBeNull();
        dto.Sku.ShouldBe(string.Empty);
        dto.UseFreeLimit.ShouldBeFalse();
        dto.FreeLimitExhaustionBehavior.ShouldBeNull();
        dto.EarliestRestoreDate.ShouldBeNull();
        dto.Location.ShouldBe(string.Empty);
    }

    [Fact]
    public void AzureDatabaseStatusDto_CanSetAllProperties()
    {
        // Arrange & Act — set all properties
        var now = DateTimeOffset.UtcNow;
        var dto = new AzureDatabaseStatusDto
        {
            DatabaseName = "test_db",
            Status = "Online",
            CreationDate = now,
            MaxSizeBytes = 34359738368,
            Sku = "GeneralPurpose / GP_S_Gen5",
            UseFreeLimit = true,
            FreeLimitExhaustionBehavior = "AutoPause",
            EarliestRestoreDate = now.AddDays(-7),
            Location = "West Europe"
        };

        // Assert
        dto.DatabaseName.ShouldBe("test_db");
        dto.Status.ShouldBe("Online");
        dto.CreationDate.ShouldBe(now);
        dto.MaxSizeBytes.ShouldBe(34359738368);
        dto.Sku.ShouldBe("GeneralPurpose / GP_S_Gen5");
        dto.UseFreeLimit.ShouldBeTrue();
        dto.FreeLimitExhaustionBehavior.ShouldBe("AutoPause");
        dto.EarliestRestoreDate.ShouldNotBeNull();
        dto.Location.ShouldBe("West Europe");
    }

    #endregion

    #region Identity Name Validation Tests

    [Fact]
    public async Task AzureSqlService_MissingFunctionAppIdentityName_ThrowsInvalidOperationException()
    {
        // Arrange — configuration WITHOUT FunctionAppIdentityName
        var configWithoutIdentity = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureSettings:SubscriptionId"] = "sub-id",
                // FunctionAppIdentityName is intentionally missing
            })
            .Build();

        var service = new AzureSqlService(configWithoutIdentity, _logger);

        // Act & Assert — should throw because FunctionAppIdentityName is not configured
        var act = () => service.CreateContainedUserAsync(
            "Server=localhost;Database=test;User Id=sa;Password=test;TrustServerCertificate=true");
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("FunctionAppIdentityName");
    }

    [Theory]
    [InlineData("valid-name")]
    [InlineData("my_func_app")]
    [InlineData("MyFuncApp123")]
    [InlineData("a")]
    [InlineData("func-app-123-test")]
    public async Task AzureSqlService_ValidIdentityNames_DoNotThrowValidationError(string identityName)
    {
        // Arrange — configuration with a valid identity name
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureSettings:FunctionAppIdentityName"] = identityName
            })
            .Build();

        var service = new AzureSqlService(config, _logger);

        // Act — CreateContainedUserAsync will fail at SqlConnection.Open (no real DB),
        // but it should NOT fail at identity name validation.
        // We catch the expected SqlException to verify validation passed.
        var act = () => service.CreateContainedUserAsync(
            "Server=localhost;Database=nonexistent;User Id=sa;Password=test;TrustServerCertificate=true");

        // Assert — if we get a connection error (not InvalidOperationException about identity name),
        // then the validation passed successfully
        var ex = await Should.ThrowAsync<Exception>(act);
        ex.Message.ShouldNotContain("invalid characters");
    }

    [Theory]
    [InlineData("invalid name with spaces")]
    [InlineData("name;DROP TABLE")]
    [InlineData("name'injection")]
    [InlineData("name\"injection")]
    [InlineData("name.with.dots")]
    [InlineData("name@domain")]
    public async Task AzureSqlService_InvalidIdentityNames_ThrowInvalidOperationException(string identityName)
    {
        // Arrange — configuration with an invalid identity name (potential SQL injection)
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureSettings:FunctionAppIdentityName"] = identityName
            })
            .Build();

        var service = new AzureSqlService(config, _logger);

        // Act & Assert — should throw BEFORE attempting SQL connection
        var act = () => service.CreateContainedUserAsync(
            "Server=localhost;Database=test;User Id=sa;Password=test;TrustServerCertificate=true");
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("invalid characters");
    }

    #endregion

    #region Configuration Validation Tests

    [Fact]
    public async Task AzureSqlService_MissingSubscriptionId_ThrowsOnCreateDatabase()
    {
        // Arrange — empty configuration (missing all Azure settings)
        var emptyConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var service = new AzureSqlService(emptyConfig, _logger);

        // Act & Assert — should throw for missing configuration
        var request = new AzureCreateDatabaseRequest
        {
            DatabaseName = "test_db",
            CompanyId = 1
        };

        var act = () => service.CreateDatabaseAsync(request);
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("AzureSettings:");
    }

    [Fact]
    public async Task AzureSqlService_MissingSubscriptionId_ThrowsOnListDatabases()
    {
        // Arrange — empty configuration
        var emptyConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var service = new AzureSqlService(emptyConfig, _logger);

        // Act & Assert
        var act = () => service.ListDatabasesAsync();
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("AzureSettings:");
    }

    [Fact]
    public async Task AzureSqlService_MissingSubscriptionId_ThrowsOnGetDatabaseStatus()
    {
        // Arrange — empty configuration
        var emptyConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var service = new AzureSqlService(emptyConfig, _logger);

        // Act & Assert
        var act = () => service.GetDatabaseStatusAsync("test_db");
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("AzureSettings:");
    }

    #endregion

    #region Controller Flow Tests (with mocked IAzureSqlService)

    [Fact]
    public async Task ProvisionTenant_MissingSettings_ReturnsBadRequest()
    {
        // Arrange — company exists but no CompanySystemSettings
        await SeedCompanyAsync(1);

        var mockAzureSql = Substitute.For<IAzureSqlService>();
        var mockProvisioning = Substitute.For<ITenantProvisioningService>();
        var controllerLogger = Substitute.For<ILogger<InvoiceApi.API.Controller.AzureOperationController>>();

        var controller = new InvoiceApi.API.Controller.AzureOperationController(
            mockAzureSql, mockProvisioning, _masterContext, _configuration, controllerLogger);

        var request = new AzureCreateDatabaseRequest
        {
            DatabaseName = "tenant_db",
            CompanyId = 1 // Settings don't exist
        };

        // Act
        var result = await controller.ProvisionTenant(request);

        // Assert — should return BadRequest because CompanySystemSettings is missing
        result.Result.ShouldBeOfType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>();

        // Verify IAzureSqlService was never called (fail-fast before ARM API call)
        await mockAzureSql.DidNotReceive().CreateDatabaseAsync(
            Arg.Any<AzureCreateDatabaseRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionTenant_CompanyNotIssuer_ReturnsBadRequest()
    {
        // Arrange — company is NOT an issuer (regular customer)
        var client = new Client
        {
            Id = 10,
            CompanyName = "Customer Company",
            RegistrationNumber = "CUST00010",
            IsIssuer = false, // NOT an issuer
            IsActive = true,
            IsVatPayer = false,
            Address = new List<Address>(),
            Contact = new List<Contact>()
        };
        _masterContext.Client.Add(client);
        await _masterContext.SaveChangesAsync();

        // Add settings for this non-issuer company
        _masterContext.CompanySystemSettings.Add(new CompanySystemSettings
        {
            CompanyId = 10,
            DatabaseName = "tenant_10"
        });
        await _masterContext.SaveChangesAsync();

        var mockAzureSql = Substitute.For<IAzureSqlService>();
        var mockProvisioning = Substitute.For<ITenantProvisioningService>();
        var controllerLogger = Substitute.For<ILogger<InvoiceApi.API.Controller.AzureOperationController>>();

        var controller = new InvoiceApi.API.Controller.AzureOperationController(
            mockAzureSql, mockProvisioning, _masterContext, _configuration, controllerLogger);

        var request = new AzureCreateDatabaseRequest
        {
            DatabaseName = "tenant_10",
            CompanyId = 10
        };

        // Act
        var result = await controller.ProvisionTenant(request);

        // Assert — should reject non-issuer companies
        result.Result.ShouldBeOfType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>();
    }

    [Fact]
    public async Task ProvisionTenant_ValidRequest_CallsAzureSqlAndProvisioning()
    {
        // Arrange — valid company with settings
        await SeedCompanyAsync(20);
        await SeedSettingsAsync(20, isProvisioned: false, isActive: false);

        var mockAzureSql = Substitute.For<IAzureSqlService>();
        var mockProvisioning = Substitute.For<ITenantProvisioningService>();
        var controllerLogger = Substitute.For<ILogger<InvoiceApi.API.Controller.AzureOperationController>>();

        // Configure mock to return a successful status
        var expectedStatus = new AzureDatabaseStatusDto
        {
            DatabaseName = "tenant_20",
            Status = "Online",
            Sku = "GeneralPurpose / GP_S_Gen5",
            UseFreeLimit = true,
            Location = "West Europe"
        };
        mockAzureSql.CreateDatabaseAsync(Arg.Any<AzureCreateDatabaseRequest>(), Arg.Any<CancellationToken>())
            .Returns(expectedStatus);
        mockProvisioning.ProvisionTenantAsync(20, Arg.Any<CancellationToken>())
            .Returns(true);

        var controller = new InvoiceApi.API.Controller.AzureOperationController(
            mockAzureSql, mockProvisioning, _masterContext, _configuration, controllerLogger);

        var request = new AzureCreateDatabaseRequest
        {
            DatabaseName = "tenant_20",
            CompanyId = 20
        };

        // Act
        var result = await controller.ProvisionTenant(request);

        // Assert — should call both services in sequence
        await mockAzureSql.Received(1).CreateDatabaseAsync(
            Arg.Any<AzureCreateDatabaseRequest>(), Arg.Any<CancellationToken>());
        await mockAzureSql.Received(1).CreateContainedUserAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await mockProvisioning.Received(1).ProvisionTenantAsync(20, Arg.Any<CancellationToken>());

        // Verify settings were updated in master DB
        var updatedSettings = await _masterContext.CompanySystemSettings
            .FirstOrDefaultAsync(s => s.CompanyId == 20);
        updatedSettings!.DatabaseName.ShouldBe("tenant_20");
        updatedSettings.ConnectionString.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task ProvisionTenant_ContainedUserFails_StillCompletesProvisioning()
    {
        // Arrange — contained user creation will fail, but provisioning should continue
        await SeedCompanyAsync(30);
        await SeedSettingsAsync(30, isProvisioned: false, isActive: false);

        var mockAzureSql = Substitute.For<IAzureSqlService>();
        var mockProvisioning = Substitute.For<ITenantProvisioningService>();
        var controllerLogger = Substitute.For<ILogger<InvoiceApi.API.Controller.AzureOperationController>>();

        mockAzureSql.CreateDatabaseAsync(Arg.Any<AzureCreateDatabaseRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AzureDatabaseStatusDto { DatabaseName = "tenant_30", Status = "Online" });

        // Simulate contained user creation failure (e.g., AAD admin not configured)
        mockAzureSql.CreateContainedUserAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("AAD admin not configured")));

        mockProvisioning.ProvisionTenantAsync(30, Arg.Any<CancellationToken>())
            .Returns(true);

        var controller = new InvoiceApi.API.Controller.AzureOperationController(
            mockAzureSql, mockProvisioning, _masterContext, _configuration, controllerLogger);

        var request = new AzureCreateDatabaseRequest
        {
            DatabaseName = "tenant_30",
            CompanyId = 30
        };

        // Act
        var result = await controller.ProvisionTenant(request);

        // Assert — provisioning should still complete despite contained user failure
        result.Result.ShouldBeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>();
        await mockProvisioning.Received(1).ProvisionTenantAsync(30, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionTenant_AzureCreateFails_Returns500()
    {
        // Arrange — Azure ARM API will throw
        await SeedCompanyAsync(40);
        await SeedSettingsAsync(40, isProvisioned: false, isActive: false);

        var mockAzureSql = Substitute.For<IAzureSqlService>();
        var mockProvisioning = Substitute.For<ITenantProvisioningService>();
        var controllerLogger = Substitute.For<ILogger<InvoiceApi.API.Controller.AzureOperationController>>();

        mockAzureSql.CreateDatabaseAsync(Arg.Any<AzureCreateDatabaseRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AzureDatabaseStatusDto>(new Exception("Azure ARM API unavailable")));

        var controller = new InvoiceApi.API.Controller.AzureOperationController(
            mockAzureSql, mockProvisioning, _masterContext, _configuration, controllerLogger);

        var request = new AzureCreateDatabaseRequest
        {
            DatabaseName = "tenant_40",
            CompanyId = 40
        };

        // Act
        var result = await controller.ProvisionTenant(request);

        // Assert — should return 500 Internal Server Error
        var statusResult = result.Result.ShouldBeOfType<Microsoft.AspNetCore.Mvc.ObjectResult>();
        statusResult.StatusCode.ShouldBe(500);

        // Provisioning service should NOT have been called
        await mockProvisioning.DidNotReceive().ProvisionTenantAsync(
            Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region Controller Standalone Endpoints Tests

    [Fact]
    public async Task ListDatabases_ReturnsOk()
    {
        // Arrange
        var mockAzureSql = Substitute.For<IAzureSqlService>();
        var mockProvisioning = Substitute.For<ITenantProvisioningService>();
        var controllerLogger = Substitute.For<ILogger<InvoiceApi.API.Controller.AzureOperationController>>();

        var databases = new List<AzureDatabaseStatusDto>
        {
            new() { DatabaseName = "master", Status = "Online" },
            new() { DatabaseName = "tenant_1", Status = "Online", UseFreeLimit = true },
            new() { DatabaseName = "tenant_2", Status = "Paused", UseFreeLimit = true }
        };
        mockAzureSql.ListDatabasesAsync(Arg.Any<CancellationToken>()).Returns(databases);

        var controller = new InvoiceApi.API.Controller.AzureOperationController(
            mockAzureSql, mockProvisioning, _masterContext, _configuration, controllerLogger);

        // Act
        var result = await controller.ListDatabases();

        // Assert
        var okResult = result.Result.ShouldBeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>();
        var returnedDbs = okResult.Value.ShouldBeOfType<List<AzureDatabaseStatusDto>>();
        returnedDbs.Count.ShouldBe(3);
    }

    [Fact]
    public async Task GetDatabaseStatus_NotFound_Returns404()
    {
        // Arrange
        var mockAzureSql = Substitute.For<IAzureSqlService>();
        var mockProvisioning = Substitute.For<ITenantProvisioningService>();
        var controllerLogger = Substitute.For<ILogger<InvoiceApi.API.Controller.AzureOperationController>>();

        mockAzureSql.GetDatabaseStatusAsync("nonexistent", Arg.Any<CancellationToken>())
            .Returns((AzureDatabaseStatusDto?)null);

        var controller = new InvoiceApi.API.Controller.AzureOperationController(
            mockAzureSql, mockProvisioning, _masterContext, _configuration, controllerLogger);

        // Act
        var result = await controller.GetDatabaseStatus("nonexistent");

        // Assert
        result.Result.ShouldBeOfType<Microsoft.AspNetCore.Mvc.NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetDatabaseStatus_Found_ReturnsOk()
    {
        // Arrange
        var mockAzureSql = Substitute.For<IAzureSqlService>();
        var mockProvisioning = Substitute.For<ITenantProvisioningService>();
        var controllerLogger = Substitute.For<ILogger<InvoiceApi.API.Controller.AzureOperationController>>();

        var status = new AzureDatabaseStatusDto { DatabaseName = "tenant_1", Status = "Online" };
        mockAzureSql.GetDatabaseStatusAsync("tenant_1", Arg.Any<CancellationToken>()).Returns(status);

        var controller = new InvoiceApi.API.Controller.AzureOperationController(
            mockAzureSql, mockProvisioning, _masterContext, _configuration, controllerLogger);

        // Act
        var result = await controller.GetDatabaseStatus("tenant_1");

        // Assert
        var okResult = result.Result.ShouldBeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>();
        var returnedStatus = okResult.Value.ShouldBeOfType<AzureDatabaseStatusDto>();
        returnedStatus.DatabaseName.ShouldBe("tenant_1");
    }

    [Fact]
    public async Task DeleteDatabase_Found_Returns204()
    {
        // Arrange
        var mockAzureSql = Substitute.For<IAzureSqlService>();
        var mockProvisioning = Substitute.For<ITenantProvisioningService>();
        var controllerLogger = Substitute.For<ILogger<InvoiceApi.API.Controller.AzureOperationController>>();

        mockAzureSql.DeleteDatabaseAsync("tenant_1", Arg.Any<CancellationToken>()).Returns(true);

        var controller = new InvoiceApi.API.Controller.AzureOperationController(
            mockAzureSql, mockProvisioning, _masterContext, _configuration, controllerLogger);

        // Act
        var result = await controller.DeleteDatabase("tenant_1");

        // Assert
        result.ShouldBeOfType<Microsoft.AspNetCore.Mvc.NoContentResult>();
    }

    [Fact]
    public async Task DeleteDatabase_NotFound_Returns404()
    {
        // Arrange
        var mockAzureSql = Substitute.For<IAzureSqlService>();
        var mockProvisioning = Substitute.For<ITenantProvisioningService>();
        var controllerLogger = Substitute.For<ILogger<InvoiceApi.API.Controller.AzureOperationController>>();

        mockAzureSql.DeleteDatabaseAsync("nonexistent", Arg.Any<CancellationToken>()).Returns(false);

        var controller = new InvoiceApi.API.Controller.AzureOperationController(
            mockAzureSql, mockProvisioning, _masterContext, _configuration, controllerLogger);

        // Act
        var result = await controller.DeleteDatabase("nonexistent");

        // Assert
        result.ShouldBeOfType<Microsoft.AspNetCore.Mvc.NotFoundObjectResult>();
    }

    #endregion

    public void Dispose()
    {
        _masterContext.Database.EnsureDeleted();
        _masterContext.Dispose();
    }
}
