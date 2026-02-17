using InvoiceApi.Contracts.Dto.User;
using InvoiceApi.Application.Service;
using InvoiceApi.Domain.Entities;
using InvoiceApi.Domain.Enums;
using InvoiceApi.Infrastructure.Data;
using InvoiceApi.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Shouldly;

namespace InvoiceApi.Tests.Unit;

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

    /// <summary>
    /// Sets up a fresh in-memory database and mock IAuthService for each test.
    /// The mock IAuthService simulates password hashing by prefixing "HASH:" to the input.
    /// </summary>
    public UserInvitationTests()
    {
        // Fresh in-memory database for each test — prevents cross-test contamination
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new MasterDbContext(options);

        // Mock IAuthService — we don't need real BCrypt for unit tests
        _authService = Substitute.For<IAuthService>();
        _authService
            .HashPassword(Arg.Any<string>())
            .Returns(callInfo => $"HASH:{callInfo.Arg<string>()}");
        _authService
            .VerifyPassword(Arg.Any<string>(), Arg.Any<string>())
            .Returns(callInfo => callInfo.ArgAt<string>(1) == $"HASH:{callInfo.ArgAt<string>(0)}");

        _userService = new UserService(_context, _authService);

        // Seed a test company (required for non-SysAdmin users)
        SeedTestCompany();
    }

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
        result.ShouldBeTrue();

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
        result.ShouldBeFalse();

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
        result.ShouldBeFalse();
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
