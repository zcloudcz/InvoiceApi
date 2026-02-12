using AresService;
using AresService.Model;
using InvoiceApi.Application.Dto.Auth;
using InvoiceApi.Application.Service;
using InvoiceApi.Domain.Entities;
using InvoiceApi.Domain.Enums;
using InvoiceApi.Infrastructure.Data;
using InvoiceApi.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace InvoiceApi.Tests.Unit;

/// <summary>
/// Unit tests for AuthService.
/// Tests password hashing, verification, login flow, JWT token generation,
/// self-registration, email verification, and external OAuth login.
/// </summary>
public class AuthServiceTests : IDisposable
{
    private readonly MasterDbContext _context;
    private readonly AuthService _service;
    private readonly IConfiguration _configuration;
    private readonly ITenantProvisioningService _provisioningService;
    private readonly IEmailService _emailService;
    private readonly IAresService _aresService;
    private readonly ILogger<AuthService> _logger;
    private readonly ITwoFactorService _twoFactorService;

    /// <summary>
    /// The BCrypt hash used in seed data for the default admin password "Invoice123".
    /// This must match the hash in MasterDbContext.SeedData and migrations.
    /// </summary>
    private const string SeedPasswordHash = "$2a$12$aI/Mx3cUBwheuL1U1laUee1OLR92DaWxdu3SLMauc5zWy7VoVwEAu";

    public AuthServiceTests()
    {
        // Fresh in-memory database for each test
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new MasterDbContext(options);

        // JWT configuration needed for token generation
        var configData = new Dictionary<string, string?>
        {
            ["JwtSettings:Secret"] = "ThisIsAVeryLongSecretKeyForTestingPurposesOnly1234567890",
            ["JwtSettings:Issuer"] = "InvoiceApiTest",
            ["JwtSettings:Audience"] = "InvoiceApiTestClient",
            ["JwtSettings:ExpirationHours"] = "24"
        };
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        // Create NSubstitute mocks for new dependencies
        _provisioningService = Substitute.For<ITenantProvisioningService>();
        _emailService = Substitute.For<IEmailService>();
        _aresService = Substitute.For<IAresService>();
        _logger = Substitute.For<ILogger<AuthService>>();
        _twoFactorService = Substitute.For<ITwoFactorService>();

        _service = new AuthService(
            _context,
            _configuration,
            _provisioningService,
            _emailService,
            _aresService,
            _logger,
            _twoFactorService);

        SeedTestData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private void SeedTestData()
    {
        // Seed a user with the SAME hash that is used in the actual database migrations.
        // This ensures the test validates the real production credentials.
        _context.User.Add(new User
        {
            Id = 1,
            Email = "admin@zcloud.cz",
            PasswordHash = SeedPasswordHash,
            FirstName = "System",
            LastName = "Administrator",
            Role = EUserRole.SysAdmin,
            CompanyId = null,
            IsActive = true,
            IsEmailVerified = true // SysAdmin is pre-verified
        });

        // Add an inactive user for testing login rejection
        _context.User.Add(new User
        {
            Id = 2,
            Email = "inactive@zcloud.cz",
            PasswordHash = SeedPasswordHash,
            FirstName = "Inactive",
            LastName = "User",
            Role = EUserRole.User,
            CompanyId = null,
            IsActive = false,
            IsEmailVerified = true
        });

        _context.SaveChanges();
    }

    // ─── Existing Tests (Password, Login, JWT) ───────────────────────────────

    /// <summary>
    /// Tests that the default admin credentials (admin@zcloud.cz / Invoice123)
    /// work with the BCrypt hash stored in seed data.
    /// </summary>
    [Fact]
    public void SeedPasswordHash_MatchesDefaultPassword()
    {
        var result = BCrypt.Net.BCrypt.Verify("Invoice123", SeedPasswordHash);
        result.ShouldBeTrue();
    }

    [Fact]
    public void VerifyPassword_DefaultCredentials_ReturnsTrue()
    {
        var result = _service.VerifyPassword("Invoice123", SeedPasswordHash);
        result.ShouldBeTrue();
    }

    [Fact]
    public void VerifyPassword_WrongPassword_ReturnsFalse()
    {
        var result = _service.VerifyPassword("WrongPassword", SeedPasswordHash);
        result.ShouldBeFalse();
    }

    [Fact]
    public void HashPassword_ProducesVerifiableHash()
    {
        var password = "TestPassword123";
        var hash = _service.HashPassword(password);

        hash.ShouldNotBeNullOrEmpty();
        BCrypt.Net.BCrypt.Verify(password, hash).ShouldBeTrue();
    }

    [Fact]
    public async Task LoginAsync_ValidCredentials_ReturnsTokenAndUserInfo()
    {
        var loginRequest = new LoginRequest
        {
            Email = "admin@zcloud.cz",
            Password = "Invoice123"
        };

        var result = await _service.LoginAsync(loginRequest);

        result.ShouldNotBeNull();
        result!.Token.ShouldNotBeNullOrEmpty();
        result.Email.ShouldBe("admin@zcloud.cz");
        result.FullName.ShouldBe("System Administrator");
        result.Role.ShouldBe(EUserRole.SysAdmin);
        result.UserId.ShouldBe(1);
        result.ExpiresAt.ShouldBeGreaterThan(DateTime.UtcNow);
    }

    [Fact]
    public async Task LoginAsync_WrongPassword_ReturnsNull()
    {
        var loginRequest = new LoginRequest
        {
            Email = "admin@zcloud.cz",
            Password = "WrongPassword"
        };

        var result = await _service.LoginAsync(loginRequest);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task LoginAsync_NonExistentEmail_ReturnsNull()
    {
        var loginRequest = new LoginRequest
        {
            Email = "nobody@zcloud.cz",
            Password = "Invoice123"
        };

        var result = await _service.LoginAsync(loginRequest);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task LoginAsync_InactiveUser_ReturnsNull()
    {
        var loginRequest = new LoginRequest
        {
            Email = "inactive@zcloud.cz",
            Password = "Invoice123"
        };

        var result = await _service.LoginAsync(loginRequest);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task LoginAsync_ValidCredentials_UpdatesLastLoginAt()
    {
        var loginRequest = new LoginRequest
        {
            Email = "admin@zcloud.cz",
            Password = "Invoice123"
        };
        var beforeLogin = DateTime.UtcNow;

        await _service.LoginAsync(loginRequest);

        var user = await _context.User.FindAsync(1L);
        user!.LastLoginAt.ShouldNotBeNull();
        user.LastLoginAt!.Value.ShouldBeGreaterThanOrEqualTo(beforeLogin);
    }

    [Fact]
    public async Task GenerateJwtTokenAsync_ValidUser_ReturnsJwtToken()
    {
        var token = await _service.GenerateJwtTokenAsync(1);

        token.ShouldNotBeNullOrEmpty();
        token.Split('.').Length.ShouldBe(3);
    }

    [Fact]
    public async Task GenerateJwtTokenAsync_InvalidUserId_ThrowsException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.GenerateJwtTokenAsync(999));
    }

    // ─── Registration Tests ──────────────────────────────────────────────────

    /// <summary>
    /// Happy path: register creates User, Client, and CompanySystemSettings.
    /// </summary>
    [Fact]
    public async Task Register_ValidRequest_CreatesUserAndCompany()
    {
        // Arrange
        var request = new RegisterRequest
        {
            Email = "newuser@example.com",
            Password = "SecurePass123",
            FirstName = "Jane",
            LastName = "Doe",
            CompanyName = "New Corp"
        };

        // Act
        var result = await _service.RegisterAsync(request, "https://app.example.com");

        // Assert
        result.ShouldNotBeNull();
        result.Email.ShouldBe("newuser@example.com");
        result.RequiresEmailVerification.ShouldBeTrue();

        // Verify user was created in DB
        var user = await _context.User.FirstOrDefaultAsync(u => u.Email == "newuser@example.com");
        user.ShouldNotBeNull();
        user!.FirstName.ShouldBe("Jane");
        user.LastName.ShouldBe("Doe");
        user.Role.ShouldBe(EUserRole.Admin);
        user.IsEmailVerified.ShouldBeFalse();
        user.EmailVerificationToken.ShouldNotBeNull();

        // Verify company (client) was created
        var client = await _context.Client.FirstOrDefaultAsync(c => c.CompanyName == "New Corp");
        client.ShouldNotBeNull();
        client!.IsIssuer.ShouldBeTrue();

        // Verify company system settings were created
        var settings = await _context.CompanySystemSettings
            .FirstOrDefaultAsync(s => s.CompanyId == client.Id);
        settings.ShouldNotBeNull();
        settings!.IsProvisioned.ShouldBeFalse();
        settings.DatabaseName.ShouldStartWith("invoiceapi_tenant_");
    }

    /// <summary>
    /// Duplicate email throws InvalidOperationException.
    /// </summary>
    [Fact]
    public async Task Register_DuplicateEmail_ThrowsInvalidOperation()
    {
        var request = new RegisterRequest
        {
            Email = "admin@zcloud.cz", // already exists
            Password = "SecurePass123",
            FirstName = "Jane",
            LastName = "Doe",
            CompanyName = "Dup Corp"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RegisterAsync(request, "https://app.example.com"));
    }

    /// <summary>
    /// When IČO (registration number) is provided, ARES lookup is called.
    /// </summary>
    [Fact]
    public async Task Register_WithRegistrationNumber_CallsAres()
    {
        // Setup ARES mock to return company info
        _aresService.GetCompanyInfoAsync("12345678", Arg.Any<CancellationToken>())
            .Returns(new AresCompanyInfo
            {
                CompanyName = "ARES Corp s.r.o.",
                RegistrationNumber = "12345678",
                TaxNumber = "CZ12345678"
            });

        var request = new RegisterRequest
        {
            Email = "ares@example.com",
            Password = "SecurePass123",
            FirstName = "Jan",
            LastName = "Novák",
            CompanyName = "User Entered Name",
            RegistrationNumber = "12345678"
        };

        var result = await _service.RegisterAsync(request, "https://app.example.com");

        // Verify ARES was called
        await _aresService.Received(1).GetCompanyInfoAsync("12345678", Arg.Any<CancellationToken>());

        // Verify company name from ARES was used
        var client = await _context.Client.FirstOrDefaultAsync(c => c.RegistrationNumber == "12345678");
        client.ShouldNotBeNull();
        client!.CompanyName.ShouldBe("ARES Corp s.r.o.");
        client.TaxNumber.ShouldBe("CZ12345678");
    }

    /// <summary>
    /// Verify that email verification token and expiration are set on the created user.
    /// </summary>
    [Fact]
    public async Task Register_SetsEmailVerificationToken()
    {
        var request = new RegisterRequest
        {
            Email = "verify@example.com",
            Password = "SecurePass123",
            FirstName = "Test",
            LastName = "User",
            CompanyName = "Verify Corp"
        };

        await _service.RegisterAsync(request, "https://app.example.com");

        var user = await _context.User.FirstOrDefaultAsync(u => u.Email == "verify@example.com");
        user.ShouldNotBeNull();
        user!.EmailVerificationToken.ShouldNotBeNullOrEmpty();
        user.EmailVerificationTokenExpiresAt.ShouldNotBeNull();
        user.EmailVerificationTokenExpiresAt!.Value.ShouldBeGreaterThan(DateTime.UtcNow);
    }

    /// <summary>
    /// Self-registered user gets Admin role.
    /// </summary>
    [Fact]
    public async Task Register_SetsUserRoleToAdmin()
    {
        var request = new RegisterRequest
        {
            Email = "role@example.com",
            Password = "SecurePass123",
            FirstName = "Role",
            LastName = "Test",
            CompanyName = "Role Corp"
        };

        await _service.RegisterAsync(request, "https://app.example.com");

        var user = await _context.User.FirstOrDefaultAsync(u => u.Email == "role@example.com");
        user.ShouldNotBeNull();
        user!.Role.ShouldBe(EUserRole.Admin);
    }

    // ─── Email Verification Tests ────────────────────────────────────────────

    /// <summary>
    /// Valid token sets IsEmailVerified = true and clears the token.
    /// </summary>
    [Fact]
    public async Task VerifyEmail_ValidToken_SetsEmailVerified()
    {
        // Seed an unverified user with a token
        var token = Guid.NewGuid().ToString();
        _context.User.Add(new User
        {
            Id = 100,
            Email = "unverified@example.com",
            PasswordHash = SeedPasswordHash,
            FirstName = "Unverified",
            LastName = "User",
            Role = EUserRole.Admin,
            IsActive = true,
            IsEmailVerified = false,
            EmailVerificationToken = token,
            EmailVerificationTokenExpiresAt = DateTime.UtcNow.AddHours(24)
        });
        _context.SaveChanges();

        var result = await _service.VerifyEmailAsync(token);

        result.ShouldBeTrue();
        var user = await _context.User.FindAsync(100L);
        user!.IsEmailVerified.ShouldBeTrue();
        user.EmailVerificationToken.ShouldBeNull();
        user.EmailVerificationTokenExpiresAt.ShouldBeNull();
    }

    /// <summary>
    /// Expired token returns false.
    /// </summary>
    [Fact]
    public async Task VerifyEmail_ExpiredToken_ReturnsFalse()
    {
        var token = Guid.NewGuid().ToString();
        _context.User.Add(new User
        {
            Id = 101,
            Email = "expired@example.com",
            PasswordHash = SeedPasswordHash,
            FirstName = "Expired",
            LastName = "User",
            Role = EUserRole.Admin,
            IsActive = true,
            IsEmailVerified = false,
            EmailVerificationToken = token,
            EmailVerificationTokenExpiresAt = DateTime.UtcNow.AddHours(-1) // already expired
        });
        _context.SaveChanges();

        var result = await _service.VerifyEmailAsync(token);

        result.ShouldBeFalse();
    }

    /// <summary>
    /// Non-existent token returns false.
    /// </summary>
    [Fact]
    public async Task VerifyEmail_InvalidToken_ReturnsFalse()
    {
        var result = await _service.VerifyEmailAsync("nonexistent-token-12345");
        result.ShouldBeFalse();
    }

    /// <summary>
    /// Successful verification triggers tenant provisioning.
    /// </summary>
    [Fact]
    public async Task VerifyEmail_TriggersProvisioning()
    {
        // Create a company for the user
        var client = new Client
        {
            Id = 200,
            CompanyName = "Provision Corp",
            RegistrationNumber = "PROV-001",
            IsIssuer = true,
            IsActive = true
        };
        _context.Client.Add(client);
        _context.SaveChanges();

        var token = Guid.NewGuid().ToString();
        _context.User.Add(new User
        {
            Id = 102,
            Email = "provision@example.com",
            PasswordHash = SeedPasswordHash,
            FirstName = "Provision",
            LastName = "User",
            Role = EUserRole.Admin,
            CompanyId = 200,
            IsActive = true,
            IsEmailVerified = false,
            EmailVerificationToken = token,
            EmailVerificationTokenExpiresAt = DateTime.UtcNow.AddHours(24)
        });
        _context.SaveChanges();

        // Configure provisioning mock to succeed
        _provisioningService.ProvisionTenantAsync(200, Arg.Any<CancellationToken>())
            .Returns(true);

        await _service.VerifyEmailAsync(token);

        // Verify provisioning was called with the correct company ID
        await _provisioningService.Received(1)
            .ProvisionTenantAsync(200, Arg.Any<CancellationToken>());
    }

    // ─── Login with Unverified Email / OAuth Tests ───────────────────────────

    /// <summary>
    /// User with unverified email cannot log in.
    /// </summary>
    [Fact]
    public async Task Login_UnverifiedEmail_ReturnsNull()
    {
        _context.User.Add(new User
        {
            Id = 103,
            Email = "unverified-login@example.com",
            PasswordHash = _service.HashPassword("TestPass123"),
            FirstName = "Unverified",
            LastName = "Login",
            Role = EUserRole.Admin,
            IsActive = true,
            IsEmailVerified = false // not verified
        });
        _context.SaveChanges();

        var loginRequest = new LoginRequest
        {
            Email = "unverified-login@example.com",
            Password = "TestPass123"
        };

        var result = await _service.LoginAsync(loginRequest);
        result.ShouldBeNull();
    }

    /// <summary>
    /// OAuth user (ExternalProvider != None) cannot use password login.
    /// </summary>
    [Fact]
    public async Task Login_OAuthUser_PasswordLogin_ReturnsNull()
    {
        _context.User.Add(new User
        {
            Id = 104,
            Email = "oauth@example.com",
            PasswordHash = null, // OAuth users don't have a local password
            FirstName = "OAuth",
            LastName = "User",
            Role = EUserRole.Admin,
            IsActive = true,
            IsEmailVerified = true,
            ExternalProvider = EExternalProvider.Google,
            ExternalProviderId = "google-12345"
        });
        _context.SaveChanges();

        var loginRequest = new LoginRequest
        {
            Email = "oauth@example.com",
            Password = "AnyPassword"
        };

        var result = await _service.LoginAsync(loginRequest);
        result.ShouldBeNull();
    }

    // ─── External Login Tests ────────────────────────────────────────────────

    /// <summary>
    /// Known OAuth user (by provider + providerId) gets a JWT token.
    /// </summary>
    [Fact]
    public async Task ExternalLogin_ExistingUser_ReturnsLoginResponse()
    {
        _context.User.Add(new User
        {
            Id = 105,
            Email = "google@example.com",
            PasswordHash = null,
            FirstName = "Google",
            LastName = "User",
            Role = EUserRole.Admin,
            IsActive = true,
            IsEmailVerified = true,
            ExternalProvider = EExternalProvider.Google,
            ExternalProviderId = "google-abc-123"
        });
        _context.SaveChanges();

        var dto = new ExternalLoginCallbackDto
        {
            Email = "google@example.com",
            FirstName = "Google",
            LastName = "User",
            Provider = EExternalProvider.Google,
            ProviderId = "google-abc-123"
        };

        var result = await _service.ExternalLoginAsync(dto);

        result.ShouldNotBeNull();
        result!.Token.ShouldNotBeNullOrEmpty();
        result.Email.ShouldBe("google@example.com");
        result.IsExternalLogin.ShouldBeTrue();
    }

    /// <summary>
    /// Existing local user (by email, no provider) gets OAuth account linked.
    /// </summary>
    [Fact]
    public async Task ExternalLogin_NewUserByEmail_LinksProvider()
    {
        _context.User.Add(new User
        {
            Id = 106,
            Email = "linkme@example.com",
            PasswordHash = SeedPasswordHash,
            FirstName = "Link",
            LastName = "Me",
            Role = EUserRole.Admin,
            IsActive = true,
            IsEmailVerified = true,
            ExternalProvider = EExternalProvider.None
        });
        _context.SaveChanges();

        var dto = new ExternalLoginCallbackDto
        {
            Email = "linkme@example.com",
            FirstName = "Link",
            LastName = "Me",
            Provider = EExternalProvider.Microsoft,
            ProviderId = "ms-xyz-789"
        };

        var result = await _service.ExternalLoginAsync(dto);

        result.ShouldNotBeNull();
        result!.Email.ShouldBe("linkme@example.com");

        // Verify provider was linked in DB
        var user = await _context.User.FindAsync(106L);
        user!.ExternalProvider.ShouldBe(EExternalProvider.Microsoft);
        user.ExternalProviderId.ShouldBe("ms-xyz-789");
        user.IsEmailVerified.ShouldBeTrue(); // OAuth auto-verifies
    }

    /// <summary>
    /// Unknown user (no matching provider or email) returns null — needs registration.
    /// </summary>
    [Fact]
    public async Task ExternalLogin_UnknownUser_ReturnsNull()
    {
        var dto = new ExternalLoginCallbackDto
        {
            Email = "brand-new@example.com",
            FirstName = "Brand",
            LastName = "New",
            Provider = EExternalProvider.Facebook,
            ProviderId = "fb-999-000"
        };

        var result = await _service.ExternalLoginAsync(dto);

        result.ShouldBeNull();
    }

    // ─── Two-Factor Authentication Tests ─────────────────────────────────────

    /// <summary>
    /// When 2FA is enabled for a user, LoginAsync should return RequiresTwoFactor = true
    /// with a session token instead of a JWT token. The login is not complete yet.
    /// </summary>
    [Fact]
    public async Task Login_With2FAEnabled_ReturnsRequiresTwoFactor()
    {
        // Arrange — enable 2FA on the admin user
        var user = await _context.User.FindAsync(1L);
        user!.TwoFactorEnabled = true;
        user.TwoFactorMethod = ETwoFactorMethod.Totp;
        await _context.SaveChangesAsync();

        // Mock the 2FA service to return a session token
        _twoFactorService.GenerateTwoFactorSessionTokenAsync(1L, Arg.Any<CancellationToken>())
            .Returns("encrypted-session-token-123");

        var loginRequest = new LoginRequest
        {
            Email = "admin@zcloud.cz",
            Password = "Invoice123"
        };

        // Act
        var result = await _service.LoginAsync(loginRequest);

        // Assert
        result.ShouldNotBeNull();
        result!.RequiresTwoFactor.ShouldBeTrue();
        result.TwoFactorSessionToken.ShouldBe("encrypted-session-token-123");
        result.TwoFactorMethod.ShouldBe(ETwoFactorMethod.Totp);
        result.Token.ShouldBeNullOrEmpty(); // No JWT issued yet
    }

    /// <summary>
    /// When 2FA is disabled for a user, LoginAsync should return a JWT token directly
    /// (existing behavior — no change). RequiresTwoFactor should be false by default.
    /// </summary>
    [Fact]
    public async Task Login_Without2FA_ReturnsJwtDirectly()
    {
        // Arrange — 2FA is disabled by default
        var loginRequest = new LoginRequest
        {
            Email = "admin@zcloud.cz",
            Password = "Invoice123"
        };

        // Act
        var result = await _service.LoginAsync(loginRequest);

        // Assert
        result.ShouldNotBeNull();
        result!.RequiresTwoFactor.ShouldBeFalse();
        result.TwoFactorSessionToken.ShouldBeNull();
        result.Token.ShouldNotBeNullOrEmpty(); // JWT issued directly
    }
}
