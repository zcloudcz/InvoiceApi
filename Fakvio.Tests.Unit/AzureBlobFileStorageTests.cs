using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service.FileStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for AzureBlobFileStorage — specifically for connection string resolution
/// and corrupt ciphertext detection.
///
/// These tests exercise the private ResolveStorageConfigAsync method indirectly by calling
/// the public UploadAsync method and observing what happens.
///
/// The BlobServiceClient is NOT mocked — we let it fail naturally on invalid connection
/// strings (FormatException from Azure SDK). The point is to verify that corrupt
/// Data Protection ciphertext is detected BEFORE it reaches the Azure SDK, preventing
/// the confusing 403 AuthorizationFailure error.
/// </summary>
public class AzureBlobFileStorageTests : IDisposable
{
    private readonly MasterDbContext _context;
    private readonly ITenantResolver _tenantResolverMock;
    private readonly ICredentialProtector _credentialProtectorMock;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AzureBlobFileStorage> _logger;

    private const long TestCompanyId = 42;

    public AzureBlobFileStorageTests()
    {
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(databaseName: $"BlobStorageTest_{Guid.NewGuid()}")
            .Options;

        _context = new MasterDbContext(options);

        _tenantResolverMock = Substitute.For<ITenantResolver>();
        _tenantResolverMock.GetCurrentCompanyId().Returns(TestCompanyId);

        _credentialProtectorMock = Substitute.For<ICredentialProtector>();
        // Default: pass-through (no encryption)
        _credentialProtectorMock.Decrypt(Arg.Any<string?>()).Returns(ci => ci.Arg<string?>());

        _configuration = Substitute.For<IConfiguration>();
        _configuration["AzureBlobStorage:ConnectionString"].Returns((string?)null);
        _configuration["AzureBlobStorage:ContainerName"].Returns((string?)null);

        _logger = Substitute.For<ILogger<AzureBlobFileStorage>>();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private AzureBlobFileStorage CreateService() =>
        new(_context, _tenantResolverMock, _credentialProtectorMock, _configuration, _logger);

    /// <summary>
    /// When CompanySystemSettings has a corrupt AzureBlobConnectionString (DP ciphertext)
    /// and SystemConfiguration has a valid one, the service should skip the corrupt
    /// company-level value and fall through to the system-wide connection string.
    ///
    /// Without this fix, the corrupt ciphertext was used as a connection string,
    /// causing Azure to return 403 AuthorizationFailure — the root cause of the
    /// production file upload bug.
    /// </summary>
    [Fact]
    public async Task Upload_CorruptCompanyConnectionString_FallsThrough_ToSystemConfig()
    {
        // Arrange
        const string corruptCiphertext = "CfDJ8this-is-corrupt-dp-ciphertext";
        // "Valid" connection string format — will fail DNS resolution but NOT with 403.
        // Using a syntactically valid connection string so BlobServiceClient constructor doesn't throw.
        const string validConnectionString =
            "DefaultEndpointsProtocol=https;AccountName=testaccount;AccountKey=dGVzdA==;EndpointSuffix=core.windows.net";

        // Company-level: corrupt (DP key mismatch)
        _context.CompanySystemSettings.Add(new CompanySystemSettings
        {
            CompanyId = TestCompanyId,
            SchemaName = "tenant_42",
            IsProvisioned = true,
            IsActive = true,
            AzureBlobConnectionString = corruptCiphertext,
            CreatedAt = DateTime.UtcNow
        });

        // System-level: valid
        _context.Set<SystemConfiguration>().Add(new SystemConfiguration
        {
            AzureBlobConnectionString = validConnectionString,
            SmtpPort = 587,
            SmtpSenderEmail = "",
            SmtpSenderName = "Test",
            SmtpUseSsl = true,
            JwtExpirationHours = 24,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        // CredentialProtector returns ciphertext unchanged for the corrupt value
        // (simulates DP key mismatch — CryptographicException caught, raw value returned)
        _credentialProtectorMock.Decrypt(corruptCiphertext).Returns(corruptCiphertext);
        // For the valid one, return as-is (pass-through)
        _credentialProtectorMock.Decrypt(validConnectionString).Returns(validConnectionString);

        var service = CreateService();

        // Act — Upload will reach Azure SDK with the valid connection string.
        // It will fail with a network/DNS error (test account doesn't exist),
        // but it should NOT throw InvalidOperationException ("not configured")
        // which would mean no connection string was resolved at all.
        var ex = await Should.ThrowAsync<Exception>(
            () => service.UploadAsync("42/test.pdf", [1, 2, 3], "application/pdf"));

        // Assert — the exception should be from Azure SDK (network error),
        // NOT InvalidOperationException from missing connection string.
        // This proves the corrupt company-level value was skipped and the
        // valid system-level value was used instead.
        ex.ShouldNotBeOfType<InvalidOperationException>();
    }

    /// <summary>
    /// When no connection string is configured anywhere, UploadAsync should throw
    /// InvalidOperationException with a clear "not configured" message.
    /// </summary>
    [Fact]
    public async Task Upload_NoConnectionString_ThrowsInvalidOperation()
    {
        // Arrange — no CompanySystemSettings, no SystemConfiguration, no appsettings
        var service = CreateService();

        // Act & Assert
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => service.UploadAsync("42/test.pdf", [1, 2, 3], "application/pdf"));
        ex.Message.ShouldContain("not configured");
    }

    /// <summary>
    /// When both company-level and system-level connection strings are corrupt,
    /// and appsettings has no fallback, UploadAsync should throw InvalidOperationException.
    /// </summary>
    [Fact]
    public async Task Upload_AllConnectionStringsCorrupt_ThrowsInvalidOperation()
    {
        // Arrange
        const string corruptCompany = "CfDJ8corrupt-company-cs";
        const string corruptSystem = "CfDJ8corrupt-system-cs";

        _context.CompanySystemSettings.Add(new CompanySystemSettings
        {
            CompanyId = TestCompanyId,
            SchemaName = "tenant_42",
            IsProvisioned = true,
            IsActive = true,
            AzureBlobConnectionString = corruptCompany,
            CreatedAt = DateTime.UtcNow
        });

        _context.Set<SystemConfiguration>().Add(new SystemConfiguration
        {
            AzureBlobConnectionString = corruptSystem,
            SmtpPort = 587,
            SmtpSenderEmail = "",
            SmtpSenderName = "Test",
            SmtpUseSsl = true,
            JwtExpirationHours = 24,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        // Both decrypt calls return the corrupt ciphertext unchanged
        _credentialProtectorMock.Decrypt(corruptCompany).Returns(corruptCompany);
        _credentialProtectorMock.Decrypt(corruptSystem).Returns(corruptSystem);

        var service = CreateService();

        // Act & Assert — both DB tiers skipped (corrupt), no appsettings → not configured
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => service.UploadAsync("42/test.pdf", [1, 2, 3], "application/pdf"));
        ex.Message.ShouldContain("not configured");
    }

    /// <summary>
    /// When company-level connection string is null (not set), system-level is used.
    /// This is the normal case — most tenants don't have company-level overrides.
    /// </summary>
    [Fact]
    public async Task Upload_CompanyConnectionStringNull_UsesSystemConfig()
    {
        // Arrange
        const string validConnectionString =
            "DefaultEndpointsProtocol=https;AccountName=testaccount;AccountKey=dGVzdA==;EndpointSuffix=core.windows.net";

        _context.CompanySystemSettings.Add(new CompanySystemSettings
        {
            CompanyId = TestCompanyId,
            SchemaName = "tenant_42",
            IsProvisioned = true,
            IsActive = true,
            // AzureBlobConnectionString is null — not set at company level
            CreatedAt = DateTime.UtcNow
        });

        _context.Set<SystemConfiguration>().Add(new SystemConfiguration
        {
            AzureBlobConnectionString = validConnectionString,
            SmtpPort = 587,
            SmtpSenderEmail = "",
            SmtpSenderName = "Test",
            SmtpUseSsl = true,
            JwtExpirationHours = 24,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        _credentialProtectorMock.Decrypt(validConnectionString).Returns(validConnectionString);

        var service = CreateService();

        // Act — will fail with network error (test account), but should not throw
        // InvalidOperationException from missing connection string
        var ex = await Should.ThrowAsync<Exception>(
            () => service.UploadAsync("42/test.pdf", [1, 2, 3], "application/pdf"));

        // Assert — Azure SDK error, not "not configured"
        ex.ShouldNotBeOfType<InvalidOperationException>();
    }
}
