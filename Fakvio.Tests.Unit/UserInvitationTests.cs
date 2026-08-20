using Fakvio.Contracts.Dto.User;
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
/// Unit tests for the user invitation flow.
/// Tests InviteUserAsync, SetPasswordAsync, and ValidateInvitationTokenAsync
/// in the UserService to ensure the invitation lifecycle works correctly.
/// </summary>
public class UserInvitationTests : IDisposable
{
    private readonly MasterDbContext _context;
    private readonly UserService _userService;
    private readonly IAuthService _authService;
    private readonly ITenantProvisioningService _provisioningService;

    /// <summary>
    /// Name of the in-memory database backing <see cref="_context"/>.
    /// Kept so a test can open a second context over the same data — needed when the
    /// test itself kills <see cref="_context"/> to simulate a lost database connection.
    /// </summary>
    private readonly string _databaseName;

    /// <summary>
    /// Sets up a fresh in-memory database and mock IAuthService for each test.
    /// The mock IAuthService simulates password hashing by prefixing "HASH:" to the input.
    /// </summary>
    public UserInvitationTests()
    {
        // Fresh in-memory database for each test — prevents cross-test contamination
        _databaseName = Guid.NewGuid().ToString();
        _context = new MasterDbContext(CreateContextOptions());

        // Mock IAuthService — we don't need real BCrypt for unit tests
        _authService = Substitute.For<IAuthService>();
        _authService
            .HashPassword(Arg.Any<string>())
            .Returns(callInfo => $"HASH:{callInfo.Arg<string>()}");
        _authService
            .VerifyPassword(Arg.Any<string>(), Arg.Any<string>())
            .Returns(callInfo => callInfo.ArgAt<string>(1) == $"HASH:{callInfo.ArgAt<string>(0)}");

        // Mock tenant provisioning and logger for UserService constructor
        _provisioningService = Substitute.For<ITenantProvisioningService>();
        var logger = Substitute.For<ILogger<UserService>>();

        _userService = new UserService(_context, _authService, _provisioningService, logger);

        // Seed a test company (required for non-SysAdmin users)
        SeedTestCompany();
    }

    /// <summary>
    /// Options pointing at this test's in-memory database. Used for the shared context
    /// and for any extra context a test needs over the very same data.
    /// </summary>
    private DbContextOptions<MasterDbContext> CreateContextOptions()
        => new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(databaseName: _databaseName)
            .Options;

    /// <summary>
    /// Creates a test company in the in-memory database.
    /// This is needed because InviteUserAsync validates that the company exists.
    /// </summary>
    private void SeedTestCompany()
    {
        _context.Client.Add(new Client
        {
            Id = 1,
            RegistrationNumber = "12345678",
            CompanyName = "Test Company",
            IsIssuer = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });

        // Every company (tenant) also owns a CompanySystemSettings row — it is created
        // at registration time with IsProvisioned = false and flipped to true only once
        // TenantProvisioningService has built the tenant schema.
        _context.CompanySystemSettings.Add(new CompanySystemSettings
        {
            CompanyId = 1,
            SchemaName = "tenant_1",
            IsProvisioned = false,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });

        _context.SaveChanges();
    }

    /// <summary>
    /// Tests that InviteUserAsync creates a user with an invitation token,
    /// sets IsInvitationPending to true, and returns a valid UserDto.
    /// </summary>
    [Fact]
    public async Task InviteUserAsync_CreatesUserWithInvitationToken()
    {
        // Arrange — prepare invitation data
        var dto = new InviteUserDto
        {
            Email = "invited@test.com",
            FirstName = "John",
            LastName = "Doe",
            Role = EUserRole.Admin,
            CompanyId = 1
        };

        // Act — invoke the invitation
        var result = await _userService.InviteUserAsync(dto);

        // Assert — verify the returned DTO
        result.ShouldNotBeNull();
        result.Email.ShouldBe("invited@test.com");
        result.FirstName.ShouldBe("John");
        result.LastName.ShouldBe("Doe");
        result.Role.ShouldBe(EUserRole.Admin);
        result.CompanyId.ShouldBe(1);
        result.IsInvitationPending.ShouldBeTrue();
        result.InvitationToken.ShouldNotBeNullOrWhiteSpace();

        // Verify the database record
        var dbUser = await _context.User.FirstOrDefaultAsync(u => u.Email == "invited@test.com");
        dbUser.ShouldNotBeNull();
        dbUser!.InvitationToken.ShouldNotBeNullOrWhiteSpace();
        dbUser.InvitationTokenExpiresAt.ShouldNotBeNull();
        dbUser.InvitationTokenExpiresAt!.Value.ShouldBeGreaterThan(DateTime.UtcNow);
        dbUser.IsInvitationPending.ShouldBeTrue();
        dbUser.PasswordHash.ShouldStartWith("HASH:");
    }

    /// <summary>
    /// Tests that InviteUserAsync throws when email already exists in the system.
    /// </summary>
    [Fact]
    public async Task InviteUserAsync_DuplicateEmail_ThrowsInvalidOperationException()
    {
        // Arrange — create existing user
        _context.User.Add(new User
        {
            Email = "existing@test.com",
            PasswordHash = "HASH:test",
            FirstName = "Existing",
            LastName = "User",
            Role = EUserRole.User,
            CompanyId = 1,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        var dto = new InviteUserDto
        {
            Email = "existing@test.com",
            FirstName = "Duplicate",
            LastName = "User",
            Role = EUserRole.User,
            CompanyId = 1
        };

        // Act & Assert — should throw because email is taken
        var act = () => _userService.InviteUserAsync(dto);
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("already exists");
    }

    /// <summary>
    /// Tests that SetPasswordAsync successfully sets the password when given a valid token,
    /// clears all invitation fields, and allows the user to log in.
    /// </summary>
    [Fact]
    public async Task SetPasswordAsync_ValidToken_SetsPasswordAndClearsInvitation()
    {
        // Arrange — create an invited user with a valid token
        var token = Guid.NewGuid().ToString();
        _context.User.Add(new User
        {
            Email = "invited@test.com",
            PasswordHash = "HASH:temporary",
            FirstName = "John",
            LastName = "Doe",
            Role = EUserRole.Admin,
            CompanyId = 1,
            IsActive = true,
            InvitationToken = token,
            InvitationTokenExpiresAt = DateTime.UtcNow.AddHours(48), // Valid for 48 hours
            IsInvitationPending = true,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        var dto = new SetPasswordDto
        {
            Token = token,
            NewPassword = "MySecurePassword123"
        };

        // Act — set the password
        var result = await _userService.SetPasswordAsync(dto);

        // Assert — password was set successfully
        result.PasswordSet.ShouldBeTrue();

        // Verify the database record — invitation fields should be cleared
        var dbUser = await _context.User.FirstOrDefaultAsync(u => u.Email == "invited@test.com");
        dbUser.ShouldNotBeNull();
        dbUser!.PasswordHash.ShouldBe("HASH:MySecurePassword123");
        dbUser.InvitationToken.ShouldBeNull();
        dbUser.InvitationTokenExpiresAt.ShouldBeNull();
        dbUser.IsInvitationPending.ShouldBeFalse();
    }

    /// <summary>
    /// Tests that SetPasswordAsync returns false when the token has expired.
    /// The user should request a new invitation from the admin.
    /// </summary>
    [Fact]
    public async Task SetPasswordAsync_ExpiredToken_ReturnsFalse()
    {
        // Arrange — create a user with an expired token (expired 1 hour ago)
        var token = Guid.NewGuid().ToString();
        _context.User.Add(new User
        {
            Email = "expired@test.com",
            PasswordHash = "HASH:temporary",
            FirstName = "Jane",
            LastName = "Doe",
            Role = EUserRole.User,
            CompanyId = 1,
            IsActive = true,
            InvitationToken = token,
            InvitationTokenExpiresAt = DateTime.UtcNow.AddHours(-1), // Already expired!
            IsInvitationPending = true,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        var dto = new SetPasswordDto
        {
            Token = token,
            NewPassword = "NewPassword123"
        };

        // Act — try to set password with expired token
        var result = await _userService.SetPasswordAsync(dto);

        // Assert — should fail because token is expired
        result.PasswordSet.ShouldBeFalse();

        // Verify the password was NOT changed
        var dbUser = await _context.User.FirstOrDefaultAsync(u => u.Email == "expired@test.com");
        dbUser!.PasswordHash.ShouldBe("HASH:temporary");
        dbUser.IsInvitationPending.ShouldBeTrue();
    }

    /// <summary>
    /// Tests that SetPasswordAsync returns false when given a non-existent token.
    /// </summary>
    [Fact]
    public async Task SetPasswordAsync_InvalidToken_ReturnsFalse()
    {
        // Arrange — no user with this token exists
        var dto = new SetPasswordDto
        {
            Token = "non-existent-token-12345",
            NewPassword = "SomePassword"
        };

        // Act
        var result = await _userService.SetPasswordAsync(dto);

        // Assert — should fail because token doesn't exist
        result.PasswordSet.ShouldBeFalse();
    }

    /// <summary>
    /// Regression test for issue #152.
    ///
    /// When tenant provisioning throws, the password must still be set (that part
    /// succeeded), but the caller must learn that the workspace is NOT usable.
    /// Before the fix the exception was swallowed and the method returned a plain
    /// "true", so the UI told the user "Done, log in" into a company with no schema.
    /// </summary>
    [Fact]
    public async Task SetPasswordAsync_ProvisioningFails_SetsPasswordButReportsWorkspaceNotReady()
    {
        // Arrange — provisioning blows up (e.g. the DB user may not CREATE SCHEMA)
        _provisioningService
            .ProvisionTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("permission denied for database"));

        var token = await SeedInvitedUserAsync("provisionfail@test.com", companyId: 1);

        var dto = new SetPasswordDto { Token = token, NewPassword = "MySecurePassword123" };

        // Act
        var result = await _userService.SetPasswordAsync(dto);

        // Assert — password part succeeded ...
        result.PasswordSet.ShouldBeTrue();

        // ... but the workspace is not ready, and the caller can see it
        result.WorkspaceReady.ShouldBeFalse();

        // The password really was persisted — the user is not locked out of the account
        var dbUser = await _context.User.FirstOrDefaultAsync(u => u.Email == "provisionfail@test.com");
        dbUser!.PasswordHash.ShouldBe("HASH:MySecurePassword123");
        dbUser.IsInvitationPending.ShouldBeFalse();
    }

    /// <summary>
    /// Counterpart to the failure case: when provisioning completes and marks the
    /// tenant as provisioned, the result reports a ready workspace.
    /// Readiness is read from the persisted CompanySystemSettings.IsProvisioned flag,
    /// not from "the call did not throw" — the flag is the single source of truth.
    /// </summary>
    [Fact]
    public async Task SetPasswordAsync_ProvisioningSucceeds_ReportsWorkspaceReady()
    {
        // Arrange — a provisioning run that marks the tenant as provisioned,
        // exactly like the real TenantProvisioningService does at the end of its pipeline.
        _provisioningService
            .ProvisionTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                var settings = await _context.CompanySystemSettings.FirstAsync(s => s.CompanyId == 1);
                settings.IsProvisioned = true;
                settings.ProvisionedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                return true;
            });

        var token = await SeedInvitedUserAsync("provisionok@test.com", companyId: 1);

        var dto = new SetPasswordDto { Token = token, NewPassword = "MySecurePassword123" };

        // Act
        var result = await _userService.SetPasswordAsync(dto);

        // Assert
        result.PasswordSet.ShouldBeTrue();
        result.WorkspaceReady.ShouldBeTrue();
    }

    /// <summary>
    /// A user without a company (SysAdmin) has no tenant schema at all,
    /// so there is nothing to provision and the workspace counts as ready.
    /// </summary>
    [Fact]
    public async Task SetPasswordAsync_UserWithoutCompany_ReportsWorkspaceReady()
    {
        // Arrange — SysAdmin has CompanyId = null
        var token = await SeedInvitedUserAsync("sysadmin@test.com", companyId: null, role: EUserRole.SysAdmin);

        var dto = new SetPasswordDto { Token = token, NewPassword = "MySecurePassword123" };

        // Act
        var result = await _userService.SetPasswordAsync(dto);

        // Assert
        result.PasswordSet.ShouldBeTrue();
        result.WorkspaceReady.ShouldBeTrue();

        // No provisioning attempt is made for company-less users
        await _provisioningService.DidNotReceive().ProvisionTenantAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The readiness check itself can fail — the master database connection can die in the
    /// window between committing the password and reading CompanySystemSettings.IsProvisioned.
    ///
    /// That must not turn into an exception: the password is already persisted, and the
    /// caller maps any failed call onto "invalid or expired token", which would send the
    /// user off to request a new invitation for a password that actually works.
    /// An unreadable state is therefore reported as "workspace not ready".
    /// </summary>
    [Fact]
    public async Task SetPasswordAsync_ProvisioningStateUnreadable_ReportsWorkspaceNotReady_WithoutThrowing()
    {
        // Arrange — the master connection dies right after provisioning ran. Disposing the
        // shared context is the in-memory equivalent: every later query on it throws.
        _provisioningService
            .ProvisionTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _context.Dispose();
                return Task.FromResult(true);
            });

        var token = await SeedInvitedUserAsync("readfail@test.com", companyId: 1);

        var dto = new SetPasswordDto { Token = token, NewPassword = "MySecurePassword123" };

        // Act — must return a result instead of propagating the read failure
        var result = await _userService.SetPasswordAsync(dto);

        // Assert — the password outcome is reported truthfully ...
        result.PasswordSet.ShouldBeTrue();

        // ... and an unknown workspace state is never reported as ready
        result.WorkspaceReady.ShouldBeFalse();

        // The password really is persisted, which is exactly why the call must not throw:
        // a thrown exception would tell this user their token had expired.
        await using var verificationContext = new MasterDbContext(CreateContextOptions());
        var dbUser = await verificationContext.User.FirstOrDefaultAsync(u => u.Email == "readfail@test.com");
        dbUser!.PasswordHash.ShouldBe("HASH:MySecurePassword123");
        dbUser.IsInvitationPending.ShouldBeFalse();
    }

    /// <summary>
    /// Helper: creates an invited user with a fresh, valid invitation token
    /// and returns that token. Keeps the provisioning tests focused on their assertions.
    /// </summary>
    private async Task<string> SeedInvitedUserAsync(string email, long? companyId, EUserRole role = EUserRole.Admin)
    {
        var token = Guid.NewGuid().ToString();

        _context.User.Add(new User
        {
            Email = email,
            PasswordHash = "HASH:temporary",
            FirstName = "Test",
            LastName = "User",
            Role = role,
            CompanyId = companyId,
            IsActive = true,
            InvitationToken = token,
            InvitationTokenExpiresAt = DateTime.UtcNow.AddHours(48),
            IsInvitationPending = true,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        return token;
    }

    /// <summary>
    /// Tests that ValidateInvitationTokenAsync returns true for a valid, non-expired token.
    /// </summary>
    [Fact]
    public async Task ValidateInvitationTokenAsync_ValidToken_ReturnsTrue()
    {
        // Arrange — create a user with a valid token
        var token = Guid.NewGuid().ToString();
        _context.User.Add(new User
        {
            Email = "valid@test.com",
            PasswordHash = "HASH:temp",
            FirstName = "Valid",
            LastName = "User",
            Role = EUserRole.User,
            CompanyId = 1,
            IsActive = true,
            InvitationToken = token,
            InvitationTokenExpiresAt = DateTime.UtcNow.AddHours(24),
            IsInvitationPending = true,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _userService.ValidateInvitationTokenAsync(token);

        // Assert — valid token should return true
        result.ShouldBeTrue();
    }

    /// <summary>
    /// Tests that ValidateInvitationTokenAsync returns false for an expired token.
    /// </summary>
    [Fact]
    public async Task ValidateInvitationTokenAsync_ExpiredToken_ReturnsFalse()
    {
        // Arrange — create a user with an expired token
        var token = Guid.NewGuid().ToString();
        _context.User.Add(new User
        {
            Email = "expired-validate@test.com",
            PasswordHash = "HASH:temp",
            FirstName = "Expired",
            LastName = "User",
            Role = EUserRole.User,
            CompanyId = 1,
            IsActive = true,
            InvitationToken = token,
            InvitationTokenExpiresAt = DateTime.UtcNow.AddHours(-1), // Already expired
            IsInvitationPending = true,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _userService.ValidateInvitationTokenAsync(token);

        // Assert — expired token should return false
        result.ShouldBeFalse();
    }

    /// <summary>
    /// Tests that ValidateInvitationTokenAsync returns false for a non-existent token.
    /// </summary>
    [Fact]
    public async Task ValidateInvitationTokenAsync_NonExistentToken_ReturnsFalse()
    {
        // Act — validate a token that doesn't exist in the database
        var result = await _userService.ValidateInvitationTokenAsync("does-not-exist-abc123");

        // Assert
        result.ShouldBeFalse();
    }

    /// <summary>
    /// Cleanup — dispose the in-memory database context after each test
    /// </summary>
    public void Dispose()
    {
        _context.Dispose();
    }
}
