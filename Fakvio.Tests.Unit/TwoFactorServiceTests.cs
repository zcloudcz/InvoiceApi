using Fakvio.Contracts.Dto.ContentTemplate;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for TwoFactorService.
/// Uses real Data Protection API with ephemeral keys (no persistent storage needed),
/// InMemoryDatabase for the MasterDbContext, and NSubstitute mocks for email/template services.
///
/// Tests cover:
/// - TOTP setup (QR code generation, secret encryption, code verification)
/// - Email 2FA enable (verified email check)
/// - Session token generation (email method sends OTP code)
/// - Code verification (valid code, invalid code, expiration, rate limiting)
/// - Disable 2FA (user self-service + admin force-disable)
/// - Status retrieval
/// </summary>
public class TwoFactorServiceTests : IDisposable
{
    private readonly MasterDbContext _context;
    private readonly TwoFactorService _service;
    private readonly IEmailService _emailService;
    private readonly IContentTemplateService _contentTemplateService;
    private readonly IDataProtectionProvider _dataProtectionProvider;

    public TwoFactorServiceTests()
    {
        // Fresh in-memory database for each test — Guid name ensures test isolation
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new MasterDbContext(options);

        // Ephemeral Data Protection provider — keys exist only in memory for testing.
        // This simulates real encryption/decryption without needing machine key storage.
        _dataProtectionProvider = DataProtectionProvider.Create("TwoFactorTests");

        // Mock dependencies
        _emailService = Substitute.For<IEmailService>();
        _contentTemplateService = Substitute.For<IContentTemplateService>();
        var logger = Substitute.For<ILogger<TwoFactorService>>();

        // Configuration with app name
        var configData = new Dictionary<string, string?>
        {
            ["AppSettings:Name"] = "Fakvio Test"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        _service = new TwoFactorService(
            _context,
            _dataProtectionProvider,
            _emailService,
            _contentTemplateService,
            configuration,
            logger);

        SeedTestData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    /// <summary>
    /// Seeds a test user with a verified email address for 2FA testing.
    /// </summary>
    private void SeedTestData()
    {
        _context.User.Add(new User
        {
            Id = 1,
            Email = "test@example.com",
            PasswordHash = "$2a$12$aI/Mx3cUBwheuL1U1laUee1OLR92DaWxdu3SLMauc5zWy7VoVwEAu",
            FirstName = "Test",
            LastName = "User",
            Role = EUserRole.User,
            IsActive = true,
            IsEmailVerified = true
        });

        // Unverified email user for email 2FA rejection test
        _context.User.Add(new User
        {
            Id = 2,
            Email = "unverified@example.com",
            PasswordHash = "$2a$12$aI/Mx3cUBwheuL1U1laUee1OLR92DaWxdu3SLMauc5zWy7VoVwEAu",
            FirstName = "Unverified",
            LastName = "User",
            Role = EUserRole.User,
            IsActive = true,
            IsEmailVerified = false
        });

        _context.SaveChanges();
    }

    // ─── TOTP Setup Tests ────────────────────────────────────────────────────

    /// <summary>
    /// Initiating TOTP setup should generate a QR code image, manual key, and otpauth:// URI.
    /// The secret is encrypted and stored in the database, but 2FA is NOT enabled yet.
    /// </summary>
    [Fact]
    public async Task InitiateTotpSetup_GeneratesQrCodeAndSecret()
    {
        // Act
        var result = await _service.InitiateTotpSetupAsync(1);

        // Assert
        result.ShouldNotBeNull();
        result.QrCodeImage.ShouldNotBeNull();
        result.QrCodeImage.ShouldNotBeEmpty();
        result.ManualEntryKey.ShouldNotBeNullOrEmpty();
        result.OtpAuthUri.ShouldContain("otpauth://totp/");
        // Email's '@' is URL-encoded to '%40' in the otpauth:// URI
        result.OtpAuthUri.ShouldContain("test%40example.com");

        // Verify secret is stored (encrypted) but 2FA is not enabled yet
        var user = await _context.User.FindAsync(1L);
        user!.TotpSecretEncrypted.ShouldNotBeNullOrEmpty();
        user.TwoFactorEnabled.ShouldBeFalse();
    }

    /// <summary>
    /// Verifying a valid TOTP code during setup should enable 2FA with TOTP method.
    /// We test this by generating a code from the secret stored during setup.
    /// </summary>
    [Fact]
    public async Task VerifyTotpSetup_ValidCode_EnablesTotp()
    {
        // Arrange — initiate setup to get the secret
        var setup = await _service.InitiateTotpSetupAsync(1);

        // Generate a valid TOTP code from the manual entry key
        var secretBytes = OtpNet.Base32Encoding.ToBytes(setup.ManualEntryKey);
        var totp = new OtpNet.Totp(secretBytes);
        var validCode = totp.ComputeTotp();

        // Act
        var result = await _service.VerifyTotpSetupAsync(1, validCode);

        // Assert
        result.ShouldBeTrue();
        var user = await _context.User.FindAsync(1L);
        user!.TwoFactorEnabled.ShouldBeTrue();
        user.TwoFactorMethod.ShouldBe(ETwoFactorMethod.Totp);
        user.TwoFactorEnabledAt.ShouldNotBeNull();
    }

    /// <summary>
    /// An invalid TOTP code during setup should return false and NOT enable 2FA.
    /// </summary>
    [Fact]
    public async Task VerifyTotpSetup_InvalidCode_ReturnsFalse()
    {
        // Arrange — initiate setup
        await _service.InitiateTotpSetupAsync(1);

        // Act — provide a wrong code
        var result = await _service.VerifyTotpSetupAsync(1, "000000");

        // Assert
        result.ShouldBeFalse();
        var user = await _context.User.FindAsync(1L);
        user!.TwoFactorEnabled.ShouldBeFalse();
    }

    // ─── Email 2FA Tests ─────────────────────────────────────────────────────

    /// <summary>
    /// Enabling email 2FA for a user with a verified email should succeed.
    /// </summary>
    [Fact]
    public async Task EnableEmailTwoFactor_SetsMethodAndEnabled()
    {
        // Act
        await _service.EnableEmailTwoFactorAsync(1);

        // Assert
        var user = await _context.User.FindAsync(1L);
        user!.TwoFactorEnabled.ShouldBeTrue();
        user.TwoFactorMethod.ShouldBe(ETwoFactorMethod.Email);
        user.TwoFactorEnabledAt.ShouldNotBeNull();
    }

    /// <summary>
    /// Enabling email 2FA for a user with an unverified email should throw.
    /// We can't send OTP codes to an unverified email address.
    /// </summary>
    [Fact]
    public async Task EnableEmailTwoFactor_UnverifiedEmail_Throws()
    {
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.EnableEmailTwoFactorAsync(2));
    }

    // ─── Session Token + Email Code Tests ────────────────────────────────────

    /// <summary>
    /// Generating a session token for email 2FA should store a hashed code
    /// and send an email with the OTP code.
    /// </summary>
    [Fact]
    public async Task GenerateSessionToken_EmailMethod_StoresHashedCode()
    {
        // Arrange — enable email 2FA first
        await _service.EnableEmailTwoFactorAsync(1);

        // Mock the content template — return null to use fallback inline template
        _contentTemplateService.GetDefaultByTypeAsync(
            EContentTemplateType.TwoFactorEmail, Arg.Any<CancellationToken>())
            .Returns((ContentTemplateDto?)null);

        // Act
        var token = await _service.GenerateTwoFactorSessionTokenAsync(1);

        // Assert
        token.ShouldNotBeNullOrEmpty();

        var user = await _context.User.FindAsync(1L);
        user!.TwoFactorSessionToken.ShouldBe(token);
        user.TwoFactorSessionTokenExpiresAt.ShouldNotBeNull();
        user.TwoFactorEmailCode.ShouldNotBeNullOrEmpty(); // BCrypt hash
        user.TwoFactorEmailCodeExpiresAt.ShouldNotBeNull();
        user.FailedTwoFactorAttempts.ShouldBe(0);

        // Verify email was sent
        await _emailService.Received(1).SendEmailAsync(
            "test@example.com",
            Arg.Any<string>(),
            Arg.Any<string>(),
            null,
            null,
            Arg.Any<CancellationToken>());
    }

    // ─── Code Verification Tests ─────────────────────────────────────────────

    /// <summary>
    /// Verifying a valid email OTP code should return the user ID.
    /// </summary>
    [Fact]
    public async Task VerifyCode_ValidEmailCode_ReturnsUserId()
    {
        // Arrange — enable email 2FA and generate session
        await _service.EnableEmailTwoFactorAsync(1);

        _contentTemplateService.GetDefaultByTypeAsync(
            EContentTemplateType.TwoFactorEmail, Arg.Any<CancellationToken>())
            .Returns((ContentTemplateDto?)null);

        var token = await _service.GenerateTwoFactorSessionTokenAsync(1);

        // We need to find the actual OTP code — it's BCrypt hashed in the DB,
        // so we can't read it directly. Instead, we'll use a known code approach:
        // Set the code manually for this test.
        var user = await _context.User.FindAsync(1L);
        var testCode = "123456";
        user!.TwoFactorEmailCode = BCrypt.Net.BCrypt.HashPassword(testCode, workFactor: 12);
        user.TwoFactorEmailCodeExpiresAt = DateTime.UtcNow.AddMinutes(5);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.VerifyTwoFactorCodeAsync(token, testCode);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldBe(1L);
    }

    /// <summary>
    /// An invalid code should return null and increment the failed attempt counter.
    /// </summary>
    [Fact]
    public async Task VerifyCode_InvalidCode_IncrementsFailures()
    {
        // Arrange
        await _service.EnableEmailTwoFactorAsync(1);

        _contentTemplateService.GetDefaultByTypeAsync(
            EContentTemplateType.TwoFactorEmail, Arg.Any<CancellationToken>())
            .Returns((ContentTemplateDto?)null);

        var token = await _service.GenerateTwoFactorSessionTokenAsync(1);

        // Act — provide a wrong code
        var result = await _service.VerifyTwoFactorCodeAsync(token, "999999");

        // Assert
        result.ShouldBeNull();

        var user = await _context.User.FindAsync(1L);
        user!.FailedTwoFactorAttempts.ShouldBe(1);
    }

    /// <summary>
    /// After 5 failed attempts, the session should be invalidated (rate limiting).
    /// </summary>
    [Fact]
    public async Task VerifyCode_MaxAttempts_InvalidatesSession()
    {
        // Arrange
        await _service.EnableEmailTwoFactorAsync(1);

        _contentTemplateService.GetDefaultByTypeAsync(
            EContentTemplateType.TwoFactorEmail, Arg.Any<CancellationToken>())
            .Returns((ContentTemplateDto?)null);

        var token = await _service.GenerateTwoFactorSessionTokenAsync(1);

        // Simulate 5 failed attempts by setting the counter directly
        var user = await _context.User.FindAsync(1L);
        user!.FailedTwoFactorAttempts = 5;
        await _context.SaveChangesAsync();

        // Act — attempt with any code
        var result = await _service.VerifyTwoFactorCodeAsync(token, "123456");

        // Assert — session should be invalidated
        result.ShouldBeNull();

        user = await _context.User.FindAsync(1L);
        user!.TwoFactorSessionToken.ShouldBeNull(); // Session cleared
    }

    /// <summary>
    /// An expired session token should return null.
    /// </summary>
    [Fact]
    public async Task VerifyCode_ExpiredSession_ReturnsNull()
    {
        // Arrange
        await _service.EnableEmailTwoFactorAsync(1);

        _contentTemplateService.GetDefaultByTypeAsync(
            EContentTemplateType.TwoFactorEmail, Arg.Any<CancellationToken>())
            .Returns((ContentTemplateDto?)null);

        var token = await _service.GenerateTwoFactorSessionTokenAsync(1);

        // Manually expire the session
        var user = await _context.User.FindAsync(1L);
        user!.TwoFactorSessionTokenExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.VerifyTwoFactorCodeAsync(token, "123456");

        // Assert
        result.ShouldBeNull();
    }

    // ─── Disable 2FA Tests ───────────────────────────────────────────────────

    /// <summary>
    /// Disabling 2FA should clear all 2FA-related fields.
    /// </summary>
    [Fact]
    public async Task DisableTwoFactor_ClearsAllFields()
    {
        // Arrange — enable TOTP 2FA first
        await _service.InitiateTotpSetupAsync(1);
        var setup = await _service.InitiateTotpSetupAsync(1);
        var secretBytes = OtpNet.Base32Encoding.ToBytes(setup.ManualEntryKey);
        var totp = new OtpNet.Totp(secretBytes);
        await _service.VerifyTotpSetupAsync(1, totp.ComputeTotp());

        // Verify 2FA is enabled
        var user = await _context.User.FindAsync(1L);
        user!.TwoFactorEnabled.ShouldBeTrue();

        // Act
        await _service.DisableTwoFactorAsync(1);

        // Assert
        user = await _context.User.FindAsync(1L);
        user!.TwoFactorEnabled.ShouldBeFalse();
        user.TwoFactorMethod.ShouldBe(ETwoFactorMethod.None);
        user.TotpSecretEncrypted.ShouldBeNull();
        user.TwoFactorEnabledAt.ShouldBeNull();
        user.TwoFactorSessionToken.ShouldBeNull();
    }

    // ─── Status Tests ────────────────────────────────────────────────────────

    /// <summary>
    /// GetStatus should return the current 2FA state for the user.
    /// </summary>
    [Fact]
    public async Task GetStatus_ReturnsCurrentStatus()
    {
        // Arrange — user has 2FA disabled initially
        var statusBefore = await _service.GetTwoFactorStatusAsync(1);
        statusBefore.Enabled.ShouldBeFalse();
        statusBefore.Method.ShouldBe(ETwoFactorMethod.None);

        // Enable email 2FA
        await _service.EnableEmailTwoFactorAsync(1);

        // Act
        var statusAfter = await _service.GetTwoFactorStatusAsync(1);

        // Assert
        statusAfter.Enabled.ShouldBeTrue();
        statusAfter.Method.ShouldBe(ETwoFactorMethod.Email);
        statusAfter.EnabledAt.ShouldNotBeNull();
    }
}
