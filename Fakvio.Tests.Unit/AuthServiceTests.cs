using AresService;
using AresService.Model;
using Fakvio.Contracts.Dto.Auth;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

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
            ["JwtSettings:Issuer"] = "FakvioTest",
            ["JwtSettings:Audience"] = "FakvioTestClient",
            ["JwtSettings:ExpirationHours"] = "24"
        };
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        // Create NSubstitute mocks for the service dependencies
        _emailService = Substitute.For<IEmailService>();
        _aresService = Substitute.For<IAresService>();
        _logger = Substitute.For<ILogger<AuthService>>();
        _twoFactorService = Substitute.For<ITwoFactorService>();

        _service = new AuthService(
            _context,
            _configuration,
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

    // ─── JWT Role Claim Tests ────────────────────────────────────────────────
    // These unit tests verify that AuthService.GenerateJwtTokenAsync emits the role
    // claim in a format that can be decoded correctly by a JWT handler.
    //
    // LIMITATION: These unit tests use BuildTestValidationParameters() which constructs
    // its OWN TokenValidationParameters with RoleClaimType = ClaimTypes.Role already set.
    // They therefore pass regardless of whether AddFakvioAuthentication() has this fix or not.
    //
    // They are NOT the regression guard for issue #20. The real regression guard is
    // in Fakvio.Tests.Integration/SysAdminRoleAuthorizationTests.cs — those tests
    // go through the REAL JwtBearer middleware registered by AddFakvioAuthentication.
    //
    // These unit tests are kept because they:
    //   a) Verify that AuthService emits a role claim with the correct value ("SysAdmin"/"User")
    //   b) Verify the token structure at the unit level (fast feedback for AuthService changes)
    //   c) Serve as documentation of expected token shape

    /// <summary>
    /// Verifies that GenerateJwtTokenAsync emits the role claim under ClaimTypes.Role
    /// with the correct value for a SysAdmin user.
    ///
    /// NOTE: This test uses a hand-rolled validator (BuildTestValidationParameters), not the
    /// production AddFakvioAuthentication config. It is a unit test of token content, not
    /// a regression guard for the middleware configuration. See SysAdminRoleAuthorizationTests
    /// for the integration-level regression guard.
    /// </summary>
    [Fact]
    public async Task GenerateJwtTokenAsync_SysAdminUser_TokenContainsRoleClaimType()
    {
        // Act — generate token for the seeded SysAdmin user (id=1)
        var token = await _service.GenerateJwtTokenAsync(1);

        // Decode with known-good TokenValidationParameters (not the production config).
        // This verifies what AuthService puts INTO the token.
        var validationParams = BuildTestValidationParameters();
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        handler.MapInboundClaims = true;
        var principal = handler.ValidateToken(token, validationParams, out _);

        // The role claim must be present and correct at the token-content level.
        var roleClaim = principal.FindFirst(System.Security.Claims.ClaimTypes.Role);
        roleClaim.ShouldNotBeNull("Generated JWT must carry a ClaimTypes.Role claim");
        roleClaim!.Value.ShouldBe("SysAdmin");
    }

    /// <summary>
    /// Verifies that IsInRole("SysAdmin") returns true when decoded with a
    /// correctly configured validator (MapInboundClaims = true, RoleClaimType set).
    ///
    /// NOTE: Unit-level test of token content. Not a guard against AddFakvioAuthentication
    /// misconfiguration. See SysAdminRoleAuthorizationTests for the real regression guard.
    /// </summary>
    [Fact]
    public async Task GenerateJwtTokenAsync_SysAdminUser_IsInRoleSysAdmin()
    {
        var token = await _service.GenerateJwtTokenAsync(1);

        var validationParams = BuildTestValidationParameters();
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        handler.MapInboundClaims = true;
        var principal = handler.ValidateToken(token, validationParams, out _);

        principal.IsInRole("SysAdmin").ShouldBeTrue(
            "SysAdmin token must satisfy IsInRole(\"SysAdmin\") with a correctly configured validator");
    }

    /// <summary>
    /// Verifies that a User-role token is NOT in the SysAdmin role — confirming
    /// AuthService emits the correct role value for non-SysAdmin users.
    /// </summary>
    [Fact]
    public async Task GenerateJwtTokenAsync_RegularUser_IsNotInRoleSysAdmin()
    {
        _context.User.Add(new User
        {
            Id = 999,
            Email = "regularuser@example.com",
            PasswordHash = SeedPasswordHash,
            FirstName = "Regular",
            LastName = "User",
            Role = EUserRole.User,
            IsActive = true,
            IsEmailVerified = true
        });
        _context.SaveChanges();

        var token = await _service.GenerateJwtTokenAsync(999);

        var validationParams = BuildTestValidationParameters();
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        handler.MapInboundClaims = true;
        var principal = handler.ValidateToken(token, validationParams, out _);

        principal.IsInRole("SysAdmin").ShouldBeFalse(
            "Regular users must not have SysAdmin role in their token");
        principal.IsInRole("User").ShouldBeTrue(
            "Regular user's token must carry role = User");
    }

    /// <summary>
    /// Builds TokenValidationParameters for unit tests.
    ///
    /// IMPORTANT: This helper has RoleClaimType = ClaimTypes.Role and
    /// MapInboundClaims = true (set at the handler level in the test methods).
    /// It does NOT use the production AddFakvioAuthentication config, so tests
    /// using it do NOT guard against misconfiguration in AuthenticationExtensions.cs.
    /// Keep this helper for fast unit-level token-content tests only.
    /// </summary>
    private Microsoft.IdentityModel.Tokens.TokenValidationParameters BuildTestValidationParameters()
    {
        return new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = false, // skip in unit tests — no clock dependency needed
            ValidateIssuerSigningKey = true,
            ValidIssuer = "FakvioTest",
            ValidAudience = "FakvioTestClient",
            IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
                System.Text.Encoding.UTF8.GetBytes(
                    "ThisIsAVeryLongSecretKeyForTestingPurposesOnly1234567890")),
            // This is THE fix — must match AuthenticationExtensions.cs.
            // Without it, the role claim stays under "role" (short JWT key) instead of
            // ClaimTypes.Role, and IsInRole() / [Authorize(Roles=...)] always fails.
            RoleClaimType = System.Security.Claims.ClaimTypes.Role,
            NameClaimType = System.Security.Claims.ClaimTypes.Name
        };
    }

    // ─── Registration Tests ──────────────────────────────────────────────────

    /// <summary>
    /// Happy path: register creates User (no password), Client, and CompanySystemSettings.
    /// Password is null — user sets it later via the set-password link.
    /// </summary>
    [Fact]
    public async Task Register_ValidRequest_CreatesUserAndCompany()
    {
        // Arrange — no Password field, RegistrationNumber is required
        var request = new RegisterRequest
        {
            Email = "newuser@example.com",
            FirstName = "Jane",
            LastName = "Doe",
            CompanyName = "New Corp",
            RegistrationNumber = "99999999"
        };

        // Act
        var result = await _service.RegisterAsync(request, "https://app.example.com");

        // Assert
        result.ShouldNotBeNull();
        result.Email.ShouldBe("newuser@example.com");
        result.RequiresEmailVerification.ShouldBeTrue();

        // Verify user was created in DB with no password and invitation pending
        var user = await _context.User.FirstOrDefaultAsync(u => u.Email == "newuser@example.com");
        user.ShouldNotBeNull();
        user!.FirstName.ShouldBe("Jane");
        user.LastName.ShouldBe("Doe");
        user.Role.ShouldBe(EUserRole.Admin);
        user.IsEmailVerified.ShouldBeFalse();
        user.PasswordHash.ShouldBeNull(); // No password at registration
        user.IsInvitationPending.ShouldBeTrue();
        user.InvitationToken.ShouldNotBeNull();

        // Verify company (client) was created
        var client = await _context.Client.FirstOrDefaultAsync(c => c.CompanyName == "New Corp");
        client.ShouldNotBeNull();
        client!.IsIssuer.ShouldBeTrue();

        // Verify company system settings were created
        var settings = await _context.CompanySystemSettings
            .FirstOrDefaultAsync(s => s.CompanyId == client.Id);
        settings.ShouldNotBeNull();
        settings!.IsProvisioned.ShouldBeFalse();
        settings.SchemaName.ShouldStartWith("tenant_");
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
            FirstName = "Jane",
            LastName = "Doe",
            CompanyName = "Dup Corp",
            RegistrationNumber = "88888888"
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
    /// Verify that invitation token and expiration are set on the created user (not email verification token).
    /// </summary>
    [Fact]
    public async Task Register_SetsInvitationToken()
    {
        var request = new RegisterRequest
        {
            Email = "verify@example.com",
            FirstName = "Test",
            LastName = "User",
            CompanyName = "Verify Corp",
            RegistrationNumber = "77777777"
        };

        await _service.RegisterAsync(request, "https://app.example.com");

        var user = await _context.User.FirstOrDefaultAsync(u => u.Email == "verify@example.com");
        user.ShouldNotBeNull();
        user!.InvitationToken.ShouldNotBeNullOrEmpty();
        user.InvitationTokenExpiresAt.ShouldNotBeNull();
        user.InvitationTokenExpiresAt!.Value.ShouldBeGreaterThan(DateTime.UtcNow);
        user.IsInvitationPending.ShouldBeTrue();
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
            FirstName = "Role",
            LastName = "Test",
            CompanyName = "Role Corp",
            RegistrationNumber = "66666666"
        };

        await _service.RegisterAsync(request, "https://app.example.com");

        var user = await _context.User.FirstOrDefaultAsync(u => u.Email == "role@example.com");
        user.ShouldNotBeNull();
        user!.Role.ShouldBe(EUserRole.Admin);
    }

    /// <summary>
    /// Verification email contains /set-password?token= link (not /verify-email).
    /// </summary>
    [Fact]
    public async Task Register_EmailContainsSetPasswordLink()
    {
        var request = new RegisterRequest
        {
            Email = "linktest@example.com",
            FirstName = "Link",
            LastName = "Test",
            CompanyName = "Link Corp",
            RegistrationNumber = "55555555"
        };

        await _service.RegisterAsync(request, "https://app.example.com");

        // Verify the email was sent with a set-password link
        await _emailService.Received(1).SendEmailAsync(
            "linktest@example.com",
            Arg.Is<string>(s => s.Contains("Set your password")),
            Arg.Is<string>(body => body.Contains("/set-password?token=")),
            Arg.Any<byte[]?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
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
