using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.SystemConfiguration;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
    private readonly ICredentialProtector _credentialProtector;
    private readonly IConfiguration _configuration;

    public SystemConfigurationServiceTests()
    {
        // Setup in-memory database with a unique name for test isolation
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(databaseName: $"SystemConfigTest_{Guid.NewGuid()}")
            .Options;

        _context = new MasterDbContext(options);
        var logger = Substitute.For<ILogger<SystemConfigurationService>>();

        // Pass-through credential protector — no real encryption in unit tests
        _credentialProtector = Substitute.For<ICredentialProtector>();
        _credentialProtector.Encrypt(Arg.Any<string?>()).Returns(ci => ci.Arg<string?>());
        _credentialProtector.Decrypt(Arg.Any<string?>()).Returns(ci => ci.Arg<string?>());

        // Empty configuration — individual tests can override via IConfiguration mock
        _configuration = Substitute.For<IConfiguration>();
        _configuration["AzureBlobStorage:ConnectionString"].Returns((string?)null);

        _service = new SystemConfigurationService(_context, _credentialProtector, _configuration, logger);
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

    // ─── TestAzureBlobConnectionAsync tests ──────────────────────────────────

    /// <summary>
    /// When no connection string is configured anywhere (neither DB nor appsettings),
    /// the test should return Success = false with a descriptive error.
    /// No network call is made.
    /// </summary>
    [Fact]
    public async Task TestAzureBlobConnectionAsync_NoConnectionString_ReturnsFalse()
    {
        // Arrange — DB has no AzureBlobConnectionString, appsettings returns null (mocked in ctor)
        await _service.GetAsync(); // Ensure default row exists (no connection string in it)

        // Act
        var result = await _service.TestAzureBlobConnectionAsync();

        // Assert
        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty(); // Must explain why it failed
    }

    /// <summary>
    /// When an invalid connection string is stored in SystemConfiguration,
    /// BlobServiceClient should throw and the service should catch it,
    /// returning Success = false with the Azure SDK error message.
    /// </summary>
    [Fact]
    public async Task TestAzureBlobConnectionAsync_InvalidConnectionString_ReturnsFalse()
    {
        // Arrange — store a syntactically invalid connection string in DB.
        // The credential protector is a pass-through in tests, so the raw string is stored as-is.
        _context.Set<SystemConfiguration>().Add(new SystemConfiguration
        {
            // "INVALID" is not a valid Azure Blob Storage connection string format
            AzureBlobConnectionString = "INVALID_CONNECTION_STRING",
            SmtpPort = 587,
            SmtpSenderEmail = "",
            SmtpSenderName = "Test",
            SmtpUseSsl = true,
            JwtExpirationHours = 24,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        // Act — the Azure SDK should throw when parsing the invalid connection string
        var result = await _service.TestAzureBlobConnectionAsync();

        // Assert — failure is caught and returned, not re-thrown
        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
    }

    /// <summary>
    /// Connection string from appsettings.json should be used as fallback
    /// when no connection string is stored in SystemConfiguration.
    /// An invalid appsettings value should still return Success = false (not throw).
    /// </summary>
    [Fact]
    public async Task TestAzureBlobConnectionAsync_AppsettingsFallback_InvalidString_ReturnsFalse()
    {
        // Arrange — DB has no connection string, but appsettings has an invalid one.
        // Create a fresh service with an IConfiguration that has an invalid blob connection string
        // (syntactically invalid — SDK fails at format parsing, no network call needed).
        var configWithBlob = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureBlobStorage:ConnectionString"] = "NOT_A_VALID_CONNECTION_STRING"
            })
            .Build();

        var logger = Substitute.For<ILogger<SystemConfigurationService>>();
        var serviceWithConfig = new SystemConfigurationService(_context, _credentialProtector, configWithBlob, logger);

        // Act
        var result = await serviceWithConfig.TestAzureBlobConnectionAsync();

        // Assert — SDK rejects the invalid format immediately, no network access
        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
    }

    /// <summary>
    /// When the connection string in DB is a corrupt Data Protection ciphertext
    /// (starts with "CfDJ8"), TestAzureBlobConnectionAsync should immediately return
    /// Success = false with a clear error message instead of sending garbage credentials
    /// to Azure (which would result in a confusing 403 AuthorizationFailure).
    ///
    /// This scenario happens when the Data Protection key ring changes (e.g., app restart
    /// without PersistKeysToDbContext) — CredentialProtector.Decrypt catches the
    /// CryptographicException and returns the raw ciphertext as a migration-safety fallback.
    /// </summary>
    [Fact]
    public async Task TestAzureBlobConnectionAsync_CorruptCiphertext_ReturnsFalse()
    {
        // Arrange — store a connection string that looks like encrypted DP ciphertext
        const string corruptCiphertext = "CfDJ8fake-corrupt-ciphertext-that-looks-like-dp-output";
        _context.Set<SystemConfiguration>().Add(new SystemConfiguration
        {
            AzureBlobConnectionString = corruptCiphertext,
            SmtpPort = 587,
            SmtpSenderEmail = "",
            SmtpSenderName = "Test",
            SmtpUseSsl = true,
            JwtExpirationHours = 24,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        // The pass-through protector returns the ciphertext unchanged (simulates DP key mismatch)
        _credentialProtector.Decrypt(corruptCiphertext).Returns(corruptCiphertext);

        // Act
        var result = await _service.TestAzureBlobConnectionAsync();

        // Assert — should detect corrupt ciphertext immediately, not attempt Azure connection
        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
        result.Error.ShouldContain("corrupt", Case.Insensitive);
    }

    // ─── CheckCredentialHealthAsync tests ────────────────────────────────────

    /// <summary>
    /// When no encrypted credentials are set (fresh install), the health check returns Healthy = true
    /// with an empty Issues list.
    /// Null/empty fields are skipped — "not configured" is not an error.
    /// </summary>
    [Fact]
    public async Task CheckCredentialHealthAsync_NoCredentials_ReturnsHealthy()
    {
        // Arrange — default row has no credentials set (all null)
        await _service.GetAsync();

        // IsHealthy mock: always returns true (pass-through protector: null → healthy)
        _credentialProtector.IsHealthy(Arg.Any<string?>()).Returns(true);

        // Act
        var result = await _service.CheckCredentialHealthAsync();

        // Assert
        result.Healthy.ShouldBeTrue();
        result.Issues.ShouldBeEmpty();
    }

    /// <summary>
    /// When a SystemConfiguration credential is corrupt, Healthy = false and Issues lists it.
    /// </summary>
    [Fact]
    public async Task CheckCredentialHealthAsync_CorruptSystemConfigSmtp_ReturnsIssue()
    {
        // Arrange — seed SystemConfiguration with an encrypted SMTP password
        _context.Set<SystemConfiguration>().Add(new SystemConfiguration
        {
            SmtpPassword = "CfDJ8corrupted-ciphertext",
            SmtpPort = 587,
            SmtpSenderEmail = "",
            SmtpSenderName = "Test",
            SmtpUseSsl = true,
            JwtExpirationHours = 24,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        // Configure the mock: SmtpPassword is corrupt, everything else is healthy
        _credentialProtector.IsHealthy("CfDJ8corrupted-ciphertext").Returns(false);
        _credentialProtector.IsHealthy(Arg.Is<string?>(s => s != "CfDJ8corrupted-ciphertext")).Returns(true);

        // Act
        var result = await _service.CheckCredentialHealthAsync();

        // Assert
        result.Healthy.ShouldBeFalse();
        result.Issues.Count.ShouldBe(1);
        result.Issues[0].Entity.ShouldBe("SystemConfiguration");
        result.Issues[0].Field.ShouldBe("SmtpPassword");
        result.Issues[0].CompanyId.ShouldBeNull();
        result.Issues[0].Status.ShouldBe("corrupt");
    }

    /// <summary>
    /// When a CompanySystemSettings credential is corrupt, Issues carries the CompanyId
    /// so the SysAdmin knows which tenant needs re-saving.
    /// </summary>
    [Fact]
    public async Task CheckCredentialHealthAsync_CorruptCompanySmtp_ReturnsIssueWithCompanyId()
    {
        // Arrange — seed SystemConfiguration (required by GetOrCreateAsync) + company settings
        await _service.GetAsync();

        // InMemory DB does not enforce FK constraints, so we can add without a matching Client row.
        _context.CompanySystemSettings.Add(new CompanySystemSettings
        {
            CompanyId = 42,
            SchemaName = "tenant_42",
            IsProvisioned = true,
            IsActive = true,
            SmtpPassword = "CfDJ8bad-company-smtp",
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        // Configure mock: company SmtpPassword is corrupt
        _credentialProtector.IsHealthy("CfDJ8bad-company-smtp").Returns(false);
        _credentialProtector.IsHealthy(Arg.Is<string?>(s => s != "CfDJ8bad-company-smtp")).Returns(true);

        // Act
        var result = await _service.CheckCredentialHealthAsync();

        // Assert
        result.Healthy.ShouldBeFalse();
        var issue = result.Issues.ShouldHaveSingleItem();
        issue.Entity.ShouldBe("CompanySystemSettings");
        issue.Field.ShouldBe("SmtpPassword");
        issue.CompanyId.ShouldBe(42);
        issue.Status.ShouldBe("corrupt");
    }

    /// <summary>
    /// When PaymentMatchingSystemSettings has a corrupt IMAP password, it appears in Issues.
    /// </summary>
    [Fact]
    public async Task CheckCredentialHealthAsync_CorruptImapPassword_ReturnsIssue()
    {
        // Arrange
        await _service.GetAsync();

        _context.PaymentMatchingSystemSettings.Add(new PaymentMatchingSystemSettings
        {
            IsEnabled = true,
            ImapHost = "imap.example.com",
            ImapPort = 993,
            ImapUseSsl = true,
            ImapUsername = "user@example.com",
            ImapPasswordEncrypted = "CfDJ8corrupt-imap-pass",
            ImapFolder = "INBOX",
            ProcessedFolder = "Processed",
            UnroutedFolder = "Unrouted",
            InboundDomain = "fakvio.cz",
            PollIntervalMinutes = 30,
            InboundEmailRetentionDays = 1825,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        _credentialProtector.IsHealthy("CfDJ8corrupt-imap-pass").Returns(false);
        _credentialProtector.IsHealthy(Arg.Is<string?>(s => s != "CfDJ8corrupt-imap-pass")).Returns(true);

        // Act
        var result = await _service.CheckCredentialHealthAsync();

        // Assert
        result.Healthy.ShouldBeFalse();
        var issue = result.Issues.ShouldHaveSingleItem();
        issue.Entity.ShouldBe("PaymentMatchingSystemSettings");
        issue.Field.ShouldBe("ImapPasswordEncrypted");
        issue.CompanyId.ShouldBeNull();
        issue.Status.ShouldBe("corrupt");
    }

    /// <summary>
    /// Multiple corrupt fields across different entities all appear in Issues.
    /// </summary>
    [Fact]
    public async Task CheckCredentialHealthAsync_MultipleCorruptFields_ReturnsAllIssues()
    {
        // Arrange — corrupt SMTP in system config + corrupt IMAP
        _context.Set<SystemConfiguration>().Add(new SystemConfiguration
        {
            SmtpPassword = "CfDJ8smtp-corrupt",
            AiClaudeApiKey = "CfDJ8claude-corrupt",
            SmtpPort = 587,
            SmtpSenderEmail = "",
            SmtpSenderName = "Test",
            SmtpUseSsl = true,
            JwtExpirationHours = 24,
            CreatedAt = DateTime.UtcNow
        });

        _context.PaymentMatchingSystemSettings.Add(new PaymentMatchingSystemSettings
        {
            ImapPasswordEncrypted = "CfDJ8imap-corrupt",
            ImapHost = "imap.example.com",
            ImapPort = 993,
            ImapUseSsl = true,
            ImapUsername = "u",
            ImapFolder = "INBOX",
            ProcessedFolder = "Processed",
            UnroutedFolder = "Unrouted",
            InboundDomain = "fakvio.cz",
            PollIntervalMinutes = 30,
            InboundEmailRetentionDays = 1825,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        // All three specific values are corrupt
        _credentialProtector.IsHealthy(Arg.Is<string?>(s =>
            s == "CfDJ8smtp-corrupt" || s == "CfDJ8claude-corrupt" || s == "CfDJ8imap-corrupt"))
            .Returns(false);
        _credentialProtector.IsHealthy(Arg.Is<string?>(s =>
            s != "CfDJ8smtp-corrupt" && s != "CfDJ8claude-corrupt" && s != "CfDJ8imap-corrupt"))
            .Returns(true);

        // Act
        var result = await _service.CheckCredentialHealthAsync();

        // Assert
        result.Healthy.ShouldBeFalse();
        result.Issues.Count.ShouldBe(3);
        result.Issues.ShouldContain(i => i.Entity == "SystemConfiguration" && i.Field == "SmtpPassword");
        result.Issues.ShouldContain(i => i.Entity == "SystemConfiguration" && i.Field == "AiClaudeApiKey");
        result.Issues.ShouldContain(i => i.Entity == "PaymentMatchingSystemSettings" && i.Field == "ImapPasswordEncrypted");
    }

    /// <summary>
    /// When all credentials are healthy, Healthy = true and Issues is empty.
    /// </summary>
    [Fact]
    public async Task CheckCredentialHealthAsync_AllCredentialsHealthy_ReturnsHealthyTrue()
    {
        // Arrange — seed with encrypted (healthy) credentials
        _context.Set<SystemConfiguration>().Add(new SystemConfiguration
        {
            SmtpPassword = "encrypted-but-healthy",
            SmtpPort = 587,
            SmtpSenderEmail = "",
            SmtpSenderName = "Test",
            SmtpUseSsl = true,
            JwtExpirationHours = 24,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        // All fields are healthy
        _credentialProtector.IsHealthy(Arg.Any<string?>()).Returns(true);

        // Act
        var result = await _service.CheckCredentialHealthAsync();

        // Assert
        result.Healthy.ShouldBeTrue();
        result.Issues.ShouldBeEmpty();
    }
}
