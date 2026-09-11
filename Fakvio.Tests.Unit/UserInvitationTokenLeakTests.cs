using System.Reflection;
using System.Text.Json;
using Fakvio.API.Controller;
using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.User;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Regression tests for issue #364 — privilege escalation inside a tenant.
///
/// The hole: <c>UserDto</c> carried the raw <c>InvitationToken</c>, and every user-listing
/// endpoint (<c>GET /api/user</c>, <c>GET /api/user/paged</c>, <c>GET /api/user/{id}</c>) is
/// open to any authenticated member of a company. Because
/// <c>POST /api/user/set-password</c> accepts that token anonymously, the token IS a
/// credential. And since the forgot-password flow recycles the very same field
/// (<c>IUserService.ForgotPasswordAsync</c>), a plain <c>User</c> only had to poll the list
/// endpoint until an Admin requested a password reset — then take over that Admin account.
///
/// These tests assert on the SERIALIZED payload, not on a property, so they still fail if
/// somebody reintroduces the leak through a different route (a new mapper, a derived DTO,
/// an anonymous projection).
/// </summary>
public class UserInvitationTokenLeakTests : IDisposable
{
    /// <summary>The one string that must never appear in a user-listing response.</summary>
    private const string PendingToken = "1e0b9c2f-secret-invitation-token";

    private const long SeededCompanyId = 1;
    private const long PendingUserId = 42;

    private readonly MasterDbContext _context;
    private readonly UserService _userService;

    /// <summary>
    /// Matches how ASP.NET Core serializes responses (camelCase), so the assertion sees the
    /// same bytes an attacker would get off the wire.
    /// </summary>
    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web);

    public UserInvitationTokenLeakTests()
    {
        _context = new MasterDbContext(
            new DbContextOptionsBuilder<MasterDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options);

        var authService = Substitute.For<IAuthService>();
        authService.HashPassword(Arg.Any<string>()).Returns(ci => $"HASH:{ci.Arg<string>()}");

        _userService = new UserService(
            _context,
            authService,
            Substitute.For<ITenantProvisioningService>(),
            Substitute.For<ILogger<UserService>>());

        SeedCompanyWithPendingInvitation();
    }

    public void Dispose() => _context.Dispose();

    /// <summary>
    /// One company, one user whose invitation is still pending — exactly the state in which
    /// the token is populated and therefore leakable.
    /// </summary>
    private void SeedCompanyWithPendingInvitation()
    {
        _context.Client.Add(new Client
        {
            Id = SeededCompanyId,
            RegistrationNumber = "12345678",
            CompanyName = "Test Company",
            IsIssuer = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });

        _context.User.Add(new User
        {
            Id = PendingUserId,
            Email = "pozvany@test.cz",
            PasswordHash = "HASH:placeholder",
            FirstName = "Pozvaný",
            LastName = "Kolega",
            Role = EUserRole.Admin,
            CompanyId = SeededCompanyId,
            IsActive = true,
            IsInvitationPending = true,
            InvitationToken = PendingToken,
            InvitationTokenExpiresAt = DateTime.UtcNow.AddHours(48),
            CreatedAt = DateTime.UtcNow
        });

        _context.SaveChanges();
    }

    /// <summary>
    /// The endpoint named in the issue. Serializing the paged result is what the API does,
    /// so if the token survives into this JSON it reaches the caller.
    /// </summary>
    [Fact]
    public async Task GetUsersPagedAsync_DoesNotSerializeInvitationToken()
    {
        var result = await _userService.GetUsersPagedAsync(new UserFilterDto { PageSize = 50 });

        result.Items.ShouldHaveSingleItem().Email.ShouldBe("pozvany@test.cz");
        JsonSerializer.Serialize(result, ApiJson).ShouldNotContain(PendingToken);
    }

    /// <summary>
    /// Sibling listing endpoint. It routes through the same mapper, and a fix that only
    /// covered /paged would leave this one open.
    /// </summary>
    [Fact]
    public async Task GetAllUsersAsync_DoesNotSerializeInvitationToken()
    {
        var users = await _userService.GetAllUsersAsync(SeededCompanyId);

        users.ShouldHaveSingleItem();
        JsonSerializer.Serialize(users, ApiJson).ShouldNotContain(PendingToken);
    }

    /// <summary>
    /// GET /api/user/{id} is only [Authorize]d as well — a colleague can read it for anyone
    /// in their own company, so it must not carry the token either.
    /// </summary>
    [Fact]
    public async Task GetUserByIdAsync_DoesNotSerializeInvitationToken()
    {
        var user = await _userService.GetUserByIdAsync(PendingUserId);

        user.ShouldNotBeNull();
        JsonSerializer.Serialize(user, ApiJson).ShouldNotContain(PendingToken);
    }

    /// <summary>
    /// The state flag admin UI actually needs survives the fix — otherwise the cheapest way
    /// to make the tests above pass would be to strip the invitation state entirely.
    /// </summary>
    [Fact]
    public async Task GetUserByIdAsync_StillReportsPendingInvitation()
    {
        var user = await _userService.GetUserByIdAsync(PendingUserId);

        user!.IsInvitationPending.ShouldBeTrue();
    }

    /// <summary>
    /// Belt to the braces above: the property is gone from the contract itself, so no future
    /// listing endpoint or mapper can put it back by accident.
    /// </summary>
    [Fact]
    public void UserDto_HasNoInvitationTokenProperty()
    {
        typeof(UserDto)
            .GetProperty("InvitationToken", BindingFlags.Public | BindingFlags.Instance)
            .ShouldBeNull();
    }

    /// <summary>
    /// The legitimate consumer keeps working: UserController.InviteUser needs the raw token to
    /// build the set-password link. It now arrives on the result type, while the nested
    /// UserDto — the object that goes into the HTTP response — stays clean.
    /// </summary>
    [Fact]
    public async Task InviteUserAsync_ReturnsTokenOnResultType_ButNotOnTheUserDto()
    {
        var invited = await _userService.InviteUserAsync(new InviteUserDto
        {
            Email = "novy@test.cz",
            FirstName = "Nový",
            LastName = "Kolega",
            Role = EUserRole.User,
            CompanyId = SeededCompanyId
        });

        invited.InvitationToken.ShouldNotBeNullOrWhiteSpace();
        JsonSerializer.Serialize(invited.User, ApiJson).ShouldNotContain(invited.InvitationToken);
    }

    /// <summary>
    /// The replacement seam returns the token for a genuinely pending invitation.
    /// </summary>
    [Fact]
    public async Task GetPendingInvitationTokenAsync_PendingInvitation_ReturnsToken()
    {
        var token = await _userService.GetPendingInvitationTokenAsync(PendingUserId);

        token.ShouldBe(PendingToken);
    }

    /// <summary>
    /// An expired token would be rejected by SetPasswordAsync, so handing it out would only
    /// mislead the caller.
    /// </summary>
    [Fact]
    public async Task GetPendingInvitationTokenAsync_ExpiredInvitation_ReturnsNull()
    {
        var user = await _context.User.FirstAsync(u => u.Id == PendingUserId);
        user.InvitationTokenExpiresAt = DateTime.UtcNow.AddHours(-1);
        await _context.SaveChangesAsync();

        var token = await _userService.GetPendingInvitationTokenAsync(PendingUserId);

        token.ShouldBeNull();
    }

    /// <summary>
    /// Once the password is set the token is cleared — there is nothing to hand out.
    /// </summary>
    [Fact]
    public async Task GetPendingInvitationTokenAsync_NoPendingInvitation_ReturnsNull()
    {
        var user = await _context.User.FirstAsync(u => u.Id == PendingUserId);
        user.InvitationToken = null;
        user.IsInvitationPending = false;
        await _context.SaveChangesAsync();

        var token = await _userService.GetPendingInvitationTokenAsync(PendingUserId);

        token.ShouldBeNull();
    }

    /// <summary>
    /// The narrow path must stay narrow. Admin and SysAdmin can already set any password in
    /// their scope (POST /api/user/{id}/admin-reset-password), so the token grants them
    /// nothing new — but widening this gate to plain [Authorize] would restore issue #364
    /// under a different URL.
    /// </summary>
    [Fact]
    public void GetInvitationTokenEndpoint_IsRestrictedToAdminAndSysAdmin()
    {
        var attribute = typeof(UserController)
            .GetMethod(nameof(UserController.GetInvitationToken))!
            .GetCustomAttribute<AuthorizeAttribute>();

        attribute.ShouldNotBeNull();
        attribute.Roles.ShouldBe("Admin,SysAdmin");
    }
}
