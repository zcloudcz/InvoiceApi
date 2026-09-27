using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.User;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Q6 (ADR 0001, docs/adr/0001-mcp-oauth21.md §9 — owner decision): a password change/reset
/// revokes every OAuth grant of that user; API keys are a separate, explicit credential and
/// stay untouched. Covers all three password-writing paths in <c>UserService</c>.
/// </summary>
public class UserServiceOAuthGrantRevocationTests : IDisposable
{
    private const long UserId = 1;

    private readonly MasterDbContext _context;
    private readonly IOAuthService _oauthService;
    private readonly UserService _userService;

    public UserServiceOAuthGrantRevocationTests()
    {
        _context = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var authService = Substitute.For<IAuthService>();
        authService.HashPassword(Arg.Any<string>()).Returns(ci => $"HASH:{ci.Arg<string>()}");
        authService.VerifyPassword(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => ci.ArgAt<string>(1) == $"HASH:{ci.ArgAt<string>(0)}");

        _oauthService = Substitute.For<IOAuthService>();

        _userService = new UserService(
            _context, authService, Substitute.For<ITenantProvisioningService>(), _oauthService,
            Substitute.For<ILogger<UserService>>());

        _context.User.Add(new User
        {
            Id = UserId, Email = "owner@test.cz", PasswordHash = "HASH:OldPassword1",
            FirstName = "Test", LastName = "Owner", Role = EUserRole.User,
            IsActive = true, ExternalProvider = EExternalProvider.None
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Fact]
    public async Task ChangePasswordAsync_RevokesAllOAuthGrants()
    {
        await _userService.ChangePasswordAsync(UserId,
            new ChangePasswordDto { CurrentPassword = "OldPassword1", NewPassword = "NewPassword2" });

        await _oauthService.Received(1).RevokeAllGrantsForUserAsync(
            UserId, EOAuthGrantRevokedReason.CredentialChanged, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangePasswordAsync_WithWrongCurrentPassword_DoesNotRevokeGrants()
    {
        // A rejected attempt must not have any side effect on live grants.
        await Should.ThrowAsync<InvalidOperationException>(() =>
            _userService.ChangePasswordAsync(UserId,
                new ChangePasswordDto { CurrentPassword = "WrongPassword", NewPassword = "NewPassword2" }));

        await _oauthService.DidNotReceive().RevokeAllGrantsForUserAsync(
            Arg.Any<long>(), Arg.Any<EOAuthGrantRevokedReason>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdminResetPasswordAsync_RevokesAllOAuthGrants()
    {
        await _userService.AdminResetPasswordAsync(UserId, "AdminSetPassword1");

        await _oauthService.Received(1).RevokeAllGrantsForUserAsync(
            UserId, EOAuthGrantRevokedReason.CredentialChanged, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetPasswordAsync_ForExistingUserResettingViaEmailedLink_RevokesAllOAuthGrants()
    {
        var user = await _context.User.SingleAsync(u => u.Id == UserId);
        user.InvitationToken = "reset-token";
        user.InvitationTokenExpiresAt = DateTime.UtcNow.AddHours(1);
        await _context.SaveChangesAsync();

        await _userService.SetPasswordAsync(new SetPasswordDto { Token = "reset-token", NewPassword = "ResetPassword1" });

        await _oauthService.Received(1).RevokeAllGrantsForUserAsync(
            UserId, EOAuthGrantRevokedReason.CredentialChanged, Arg.Any<CancellationToken>());
    }
}
