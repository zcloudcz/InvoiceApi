using System.Security.Claims;
using Fakvio.Contracts.Dto.ApiKey;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Authentication;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for ApiKeyAuthenticator — the shared validation both hosts run.
///
/// Two invariants these tests exist to protect:
/// 1. A key that must not work (unknown / revoked / expired / deactivated owner)
///    produces NO principal — the callers turn that into a 401.
/// 2. A key that does work produces the SAME claims the JWT path produces, so tenant
///    resolution, impersonation and [Authorize(Roles = …)] behave identically.
///
/// Uses InMemoryDatabase (master DB context), like ApiKeyServiceTests.
/// </summary>
public class ApiKeyAuthenticatorTests : IDisposable
{
    private const long OwnerUserId = 1;
    private const long SysAdminUserId = 2;
    private const long InactiveUserId = 3;
    private const long OwnerCompanyId = 10;

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly MasterDbContext _context;
    private readonly ApiKeyService _service;
    private readonly ApiKeyAuthenticator _authenticator;

    public ApiKeyAuthenticatorTests()
    {
        _context = CreateContext();
        _service = new ApiKeyService(_context, Substitute.For<ILogger<ApiKeyService>>());
        _authenticator = new ApiKeyAuthenticator(
            _context,
            Substitute.For<ILogger<ApiKeyAuthenticator>>(),
            Microsoft.Extensions.Options.Options.Create(new Fakvio.Infrastructure.Authentication.OAuth.McpOAuthOptions()));

        _context.Client.Add(new Client { Id = OwnerCompanyId, RegistrationNumber = "owner", IsIssuer = true });
        _context.User.AddRange(
            NewUser(OwnerUserId, "owner@test.cz", EUserRole.User, companyId: OwnerCompanyId, isActive: true),
            // No company — exactly the shape that makes SysAdmin impersonation possible.
            NewUser(SysAdminUserId, "sysadmin@test.cz", EUserRole.SysAdmin, companyId: null, isActive: true),
            NewUser(InactiveUserId, "fired@test.cz", EUserRole.User, companyId: OwnerCompanyId, isActive: false));
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private MasterDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options;

        return new MasterDbContext(options);
    }

    private static User NewUser(long id, string email, EUserRole role, long? companyId, bool isActive) => new()
    {
        Id = id,
        Email = email,
        FirstName = "Test",
        LastName = "User",
        Role = role,
        CompanyId = companyId,
        IsActive = isActive,
        IsEmailVerified = true,
        ExternalProvider = EExternalProvider.None
    };

    /// <summary>
    /// Creates a real key through the real service, so the tests authenticate against
    /// a hash produced the same way production produces it.
    /// </summary>
    private async Task<CreatedApiKeyDto> CreateKeyAsync(
        long userId = OwnerUserId,
        string scopes = "read",
        DateTime? expiresAt = null)
    {
        return await _service.CreateAsync(userId, new CreateApiKeyDto
        {
            Name = "test key",
            Scopes = scopes,
            ExpiresAt = expiresAt
        });
    }

    // ─── Happy path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidKey_ProducesAuthenticatedPrincipal()
    {
        var created = await CreateKeyAsync();

        var principal = await _authenticator.AuthenticateAsync(created.Key);

        principal.ShouldNotBeNull();
        // Without an authentication type on the identity, IsAuthenticated is false and
        // every [Authorize] endpoint answers 401 no matter how valid the key is.
        principal.Identity!.IsAuthenticated.ShouldBeTrue();
        principal.Identity.AuthenticationType.ShouldBe(ApiKeyAuthenticationDefaults.AuthenticationScheme);
    }

    [Fact]
    public async Task ValidKey_CarriesTheSameClaimsAsTheJwtPath()
    {
        var created = await CreateKeyAsync(scopes: "read,write");

        var principal = await _authenticator.AuthenticateAsync(created.Key);

        // Claim-for-claim mirror of AuthService.GenerateJwtTokenAsync.
        principal!.FindFirst(ClaimTypes.NameIdentifier)!.Value.ShouldBe(OwnerUserId.ToString());
        principal.FindFirst(ClaimTypes.Email)!.Value.ShouldBe("owner@test.cz");
        principal.FindFirst(ClaimTypes.Name)!.Value.ShouldBe("Test User");
        principal.FindFirst(ClaimTypes.Role)!.Value.ShouldBe("User");
        principal.FindFirst("CompanyId")!.Value.ShouldBe(OwnerCompanyId.ToString());

        // Plus the key-specific ones.
        principal.FindFirst(ApiKeyAuthenticationDefaults.ScopeClaimType)!.Value.ShouldBe("read,write");
        principal.FindFirst(ApiKeyAuthenticationDefaults.KeyIdClaimType)!.Value.ShouldBe(created.Id.ToString());
    }

    [Fact]
    public async Task ValidKey_RoleIsUsableByAuthorizeRolesAttribute()
    {
        var created = await CreateKeyAsync(userId: SysAdminUserId);

        var principal = await _authenticator.AuthenticateAsync(created.Key);

        // IsInRole only works when the identity was built with roleType: ClaimTypes.Role.
        // Without it [Authorize(Roles = "SysAdmin")] silently answers 403.
        principal!.IsInRole("SysAdmin").ShouldBeTrue();
    }

    [Fact]
    public async Task SysAdminKey_HasNoCompanyIdClaim_SoImpersonationCanSupplyIt()
    {
        var created = await CreateKeyAsync(userId: SysAdminUserId);

        var principal = await _authenticator.AuthenticateAsync(created.Key);

        // ImpersonationMiddleware only fills CompanyId in from X-Company-Id; if the
        // authenticator invented one, a SysAdmin key could never target a tenant.
        principal!.FindFirst("CompanyId").ShouldBeNull();
        principal.FindFirst(ClaimTypes.Role)!.Value.ShouldBe("SysAdmin");
    }

    [Fact]
    public async Task ValidKey_WithFutureExpiration_IsAccepted()
    {
        var created = await CreateKeyAsync(expiresAt: DateTime.UtcNow.AddDays(1));

        (await _authenticator.AuthenticateAsync(created.Key)).ShouldNotBeNull();
    }

    // ─── Fail closed ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownKey_IsRejected()
    {
        // Correctly shaped, simply never issued.
        (await _authenticator.AuthenticateAsync("fak_live_" + new string('A', 43))).ShouldBeNull();
    }

    [Fact]
    public async Task RevokedKey_IsRejected()
    {
        var created = await CreateKeyAsync();
        (await _service.RevokeAsync(OwnerUserId, created.Id)).ShouldBeTrue();

        (await _authenticator.AuthenticateAsync(created.Key)).ShouldBeNull();
    }

    [Fact]
    public async Task ExpiredKey_IsRejected()
    {
        var created = await CreateKeyAsync(expiresAt: DateTime.UtcNow.AddHours(1));

        // CreateAsync refuses a past expiration, so age the row instead of asking for one.
        await ExpireKeyAsync(created.Id, DateTime.UtcNow.AddMinutes(-1));

        (await _authenticator.AuthenticateAsync(created.Key)).ShouldBeNull();
    }

    /// <summary>
    /// The contract of the Kind normalization, stated once for all three representations
    /// a provider can hand back: whichever one the deadline arrives in, the accept/reject
    /// decision must depend on the INSTANT it denotes and on nothing else.
    ///
    /// Both directions matter and they fail differently. Reading the deadline as later
    /// than it is keeps a revoked-by-time key alive (CEST: two extra hours). Reading it
    /// as earlier retires a live key without warning (any zone west of Greenwich).
    ///
    /// Note on hosts with a zero UTC offset (a CI runner is normally one): there, local
    /// time IS UTC, so the Local rows carry the same ticks as the Utc rows and stop
    /// discriminating between converting the value and merely relabelling it. That is not
    /// a gap in the test — with a zero offset the two are the same operation, and the bug
    /// the conversion prevents cannot occur. The rows are written against
    /// TimeZoneInfo.Local rather than against DateTime.Now so the value under test is
    /// stated explicitly instead of inherited from the ambient clock.
    /// </summary>
    [Theory]
    [InlineData(DateTimeKind.Utc, -1, false)]
    [InlineData(DateTimeKind.Unspecified, -1, false)]
    [InlineData(DateTimeKind.Local, -1, false)]
    [InlineData(DateTimeKind.Utc, 1, true)]
    [InlineData(DateTimeKind.Unspecified, 1, true)]
    [InlineData(DateTimeKind.Local, 1, true)]
    public async Task ExpiryIsJudgedByTheInstant_WhicheverKindTheProviderAttached(
        DateTimeKind storedKind, int minutesFromDeadline, bool expectedUsable)
    {
        var created = await CreateKeyAsync(expiresAt: DateTime.UtcNow.AddHours(1));

        var instant = DateTime.UtcNow.AddMinutes(minutesFromDeadline);
        var stored = AsStoredBy(storedKind, instant);
        SetTrackedExpiresAt(created.Id, stored);

        var principal = await _authenticator.AuthenticateAsync(created.Key);

        (principal is not null).ShouldBe(expectedUsable,
            $"ExpiresAt read back as {stored:O} (Kind = {stored.Kind})");
    }

    [Fact]
    public async Task KeyOfDeactivatedUser_IsRejected()
    {
        var created = await CreateKeyAsync();
        (await _context.User.SingleAsync(u => u.Id == OwnerUserId)).IsActive = false;
        await _context.SaveChangesAsync();

        // The key outlives the login session, so deactivating the user has to kill it too.
        (await _authenticator.AuthenticateAsync(created.Key)).ShouldBeNull();
    }

    [Fact]
    public async Task KeyCompany_DoesNotFollowUsersDefaultCompany()
    {
        var created = await CreateKeyAsync();
        (await _context.User.SingleAsync(u => u.Id == OwnerUserId)).CompanyId = 99;
        await _context.SaveChangesAsync();
        var principal = await _authenticator.AuthenticateAsync(created.Key);
        principal.ShouldNotBeNull();
        principal.FindFirst("CompanyId")!.Value.ShouldBe(OwnerCompanyId.ToString());
    }

    [Fact]
    public async Task KeyRole_UsesLiveMembershipInsteadOfGlobalDefaultRole()
    {
        var created = await CreateKeyAsync();
        var membership = await _context.UserCompanyMembership.SingleAsync(m => m.UserId == OwnerUserId);
        membership.Role = EUserRole.Admin;
        await _context.SaveChangesAsync();
        (await _authenticator.AuthenticateAsync(created.Key))!.IsInRole("Admin").ShouldBeTrue();
        membership.Role = EUserRole.User;
        await _context.SaveChangesAsync();
        (await _authenticator.AuthenticateAsync(created.Key))!.IsInRole("Admin").ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.e30.signature")] // a JWT, not an API key
    [InlineData("fak")]                                 // shorter than the prefix
    public async Task NonApiKeyInput_IsRejectedWithoutTouchingTheDatabase(string input)
    {
        (await _authenticator.AuthenticateAsync(input)).ShouldBeNull();
    }

    // ─── LastUsedAt ──────────────────────────────────────────────────────────

    [Fact]
    public async Task FirstUse_RecordsLastUsedAt()
    {
        var created = await CreateKeyAsync();

        await _authenticator.AuthenticateAsync(created.Key);

        var stored = await ReadKeyAsync(created.Id);
        stored.LastUsedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task RepeatedUseWithinTheWriteInterval_DoesNotRewriteLastUsedAt()
    {
        var created = await CreateKeyAsync();
        await _authenticator.AuthenticateAsync(created.Key);
        var firstUse = (await ReadKeyAsync(created.Id)).LastUsedAt;

        await _authenticator.AuthenticateAsync(created.Key);

        // One AI turn is dozens of tool calls; each one must not become a master DB write.
        (await ReadKeyAsync(created.Id)).LastUsedAt.ShouldBe(firstUse);
    }

    [Fact]
    public async Task UseAfterTheWriteInterval_RefreshesLastUsedAt()
    {
        var created = await CreateKeyAsync();
        var stale = DateTime.UtcNow.AddHours(-1);
        await SetLastUsedAsync(created.Id, stale);

        await _authenticator.AuthenticateAsync(created.Key);

        (await ReadKeyAsync(created.Id)).LastUsedAt!.Value.ShouldBeGreaterThan(stale);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders one instant the way each provider hands it back:
    /// - Utc — Npgsql with the modern timestamptz mapping, and every value the write path
    ///   stamped itself;
    /// - Unspecified — a value that lost its Kind (EF InMemory, a JSON round-trip). The
    ///   repo stores UTC wall-clock everywhere (MasterDbContext.NormalizeDateTimesToUtc),
    ///   so the ticks are the UTC ones and only the label is missing;
    /// - Local — Npgsql under EnableLegacyTimestampBehavior, which both hosts switch on:
    ///   the instant CONVERTED into the host's zone. Built from the zone's offset rather
    ///   than DateTime.ToLocalTime() so the test says which value it means.
    /// </summary>
    private static DateTime AsStoredBy(DateTimeKind kind, DateTime instant) => kind switch
    {
        DateTimeKind.Utc => instant,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(instant, DateTimeKind.Unspecified),
        _ => DateTime.SpecifyKind(instant + TimeZoneInfo.Local.GetUtcOffset(instant), DateTimeKind.Local)
    };

    /// <summary>
    /// Rewrites ExpiresAt directly. Bypasses CreateAsync on purpose — the service refuses
    /// to mint an already-expired key, which is exactly the state we need to test.
    /// </summary>
    private async Task ExpireKeyAsync(long id, DateTime expiresAt)
    {
        await using var context = CreateContext();
        var key = await context.ApiKey.FirstAsync(k => k.Id == id);
        key.ExpiresAt = expiresAt;
        await context.SaveChangesAsync();
        DetachFromAuthenticatorContext(id);
    }

    /// <summary>
    /// Sets ExpiresAt on the instance the authenticator's own context is tracking, WITHOUT
    /// saving. Saving would run MasterDbContext.NormalizeDateTimesToUtc, which relabels the
    /// value as UTC — and the kind is the whole point of the tests that use this. The query
    /// inside the authenticator resolves to this same tracked instance, so it sees exactly
    /// the value set here, which is how the driver's Local-kind read-back is reproduced
    /// without a real PostgreSQL.
    /// </summary>
    private void SetTrackedExpiresAt(long id, DateTime expiresAt)
        => _context.ApiKey.Local.First(k => k.Id == id).ExpiresAt = expiresAt;

    private async Task SetLastUsedAsync(long id, DateTime lastUsedAt)
    {
        await using var context = CreateContext();
        var key = await context.ApiKey.FirstAsync(k => k.Id == id);
        key.LastUsedAt = lastUsedAt;
        await context.SaveChangesAsync();
        DetachFromAuthenticatorContext(id);
    }

    private async Task<Domain.Entities.ApiKey> ReadKeyAsync(long id)
    {
        await using var context = CreateContext();
        return await context.ApiKey.AsNoTracking().FirstAsync(k => k.Id == id);
    }

    /// <summary>
    /// The authenticator shares its context with the service that created the key, so the
    /// row is still tracked there and a query would return the stale tracked instance
    /// instead of what the side context just wrote.
    /// </summary>
    private void DetachFromAuthenticatorContext(long id)
    {
        var tracked = _context.ChangeTracker.Entries<Domain.Entities.ApiKey>()
            .FirstOrDefault(e => e.Entity.Id == id);

        if (tracked is not null)
            tracked.State = EntityState.Detached;
    }
}
