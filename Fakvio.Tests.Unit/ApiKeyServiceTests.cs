using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Fakvio.Contracts.Dto.ApiKey;
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
/// Unit tests for ApiKeyService — key generation, hashing, scope normalization,
/// expiration validation and revocation.
///
/// The security-critical invariant these tests exist to protect: the raw key is
/// returned exactly once and is NEVER recoverable from the database.
/// Uses InMemoryDatabase (master DB context).
/// </summary>
public class ApiKeyServiceTests : IDisposable
{
    private const long OwnerUserId = 1;
    private const long OtherUserId = 2;
    private const long SysAdminUserId = 3;

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly MasterDbContext _context;
    private readonly ApiKeyService _service;

    public ApiKeyServiceTests()
    {
        _context = CreateContext();
        _service = new ApiKeyService(_context, Substitute.For<ILogger<ApiKeyService>>());

        // FK targets — an API key always belongs to a user.
        _context.User.AddRange(
            NewUser(OwnerUserId, "owner@test.cz", EUserRole.User, companyId: 10),
            NewUser(OtherUserId, "other@test.cz", EUserRole.User, companyId: 20),
            // SysAdmin has no company — the key must not require one, otherwise
            // a SysAdmin key could never impersonate a tenant (story #144 default).
            NewUser(SysAdminUserId, "sysadmin@test.cz", EUserRole.SysAdmin, companyId: null));
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

    private static User NewUser(long id, string email, EUserRole role, long? companyId) => new()
    {
        Id = id,
        Email = email,
        FirstName = "Test",
        LastName = "User",
        Role = role,
        CompanyId = companyId,
        IsActive = true,
        IsEmailVerified = true,
        ExternalProvider = EExternalProvider.None
    };

    /// <summary>
    /// Reads the stored row through a SECOND context so the assertions see what is
    /// really persisted, not the tracked instance the service just wrote.
    /// </summary>
    private async Task<ApiKey> LoadStoredKeyAsync(long id)
    {
        await using var verification = CreateContext();
        return await verification.ApiKey.AsNoTracking().SingleAsync(k => k.Id == id);
    }

    private static CreateApiKeyDto NewRequest(string name = "Claude Desktop", string scopes = "read")
        => new() { Name = name, Scopes = scopes };

    // ─── Key generation & hashing ─────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ReturnsRawKeyWithFakPrefix()
    {
        var created = await _service.CreateAsync(OwnerUserId, NewRequest());

        // "fak_" is load-bearing: the auth scheme selector (#236) distinguishes an
        // API key from a JWT ("eyJ…") by it.
        created.Key.ShouldStartWith("fak_live_");

        // 9 chars prefix + 43 chars of Base64Url over 32 bytes.
        created.Key.Length.ShouldBe(52);
        Regex.IsMatch(created.Key, "^fak_live_[A-Za-z0-9_-]{43}$").ShouldBeTrue(created.Key);
    }

    [Fact]
    public async Task CreateAsync_StoresOnlySha256Hash_NeverTheRawKey()
    {
        var created = await _service.CreateAsync(OwnerUserId, NewRequest());
        var stored = await LoadStoredKeyAsync(created.Id);

        var expectedHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(created.Key)));
        stored.KeyHash.ShouldBe(expectedHash);

        // The single most important assertion in this file: nothing persisted can be
        // replayed as a credential.
        stored.KeyHash.ShouldNotBe(created.Key);
        stored.KeyPrefix.ShouldNotBe(created.Key);
        stored.Name.ShouldNotContain(created.Key);
    }

    [Fact]
    public async Task CreateAsync_KeyPrefixIsFirst12CharsOfRawKey()
    {
        var created = await _service.CreateAsync(OwnerUserId, NewRequest());
        var stored = await LoadStoredKeyAsync(created.Id);

        stored.KeyPrefix.ShouldBe(created.Key[..12]);
        // 12 of 52 characters — enough to tell keys apart, far too little to guess the rest.
        stored.KeyPrefix.Length.ShouldBe(12);
        created.KeyPrefix.ShouldBe(stored.KeyPrefix);
    }

    [Fact]
    public async Task CreateAsync_TwoKeys_AreDifferent()
    {
        var first = await _service.CreateAsync(OwnerUserId, NewRequest("first"));
        var second = await _service.CreateAsync(OwnerUserId, NewRequest("second"));

        second.Key.ShouldNotBe(first.Key);

        var storedFirst = await LoadStoredKeyAsync(first.Id);
        var storedSecond = await LoadStoredKeyAsync(second.Id);
        storedSecond.KeyHash.ShouldNotBe(storedFirst.KeyHash);
    }

    [Fact]
    public void ComputeHash_IsDeterministic_AndDiffersPerKey()
    {
        // Determinism is what makes KeyHash indexable — authentication (#236) must be
        // one indexed equality lookup, not a scan with a verify per row.
        ApiKeyService.ComputeHash("fak_live_abc").ShouldBe(ApiKeyService.ComputeHash("fak_live_abc"));
        ApiKeyService.ComputeHash("fak_live_abc").ShouldNotBe(ApiKeyService.ComputeHash("fak_live_abd"));
    }

    // ─── Scopes ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("read", "read")]
    [InlineData("READ", "read")]
    [InlineData("read,write", "read,write")]
    [InlineData(" read , write ", "read,write")]
    [InlineData("write,read", "read,write")]
    // "write" alone is normalized up to "read,write" — a write-only key could not read
    // back what it wrote and no client wants one.
    [InlineData("write", "read,write")]
    public async Task CreateAsync_NormalizesScopes(string requested, string expected)
    {
        var created = await _service.CreateAsync(OwnerUserId, NewRequest(scopes: requested));

        created.Scopes.ShouldBe(expected);
        (await LoadStoredKeyAsync(created.Id)).Scopes.ShouldBe(expected);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("read,delete")]
    [InlineData("")]
    [InlineData(" , ")]
    // Numeric input must not pass either: Enum.TryParse accepts the underlying
    // number, so "1" would silently mean Write and "999" an enum value that does
    // not exist — both would land in the Scopes column without ever meeting the
    // allow-list.
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("999")]
    public async Task CreateAsync_InvalidScopes_Throws(string scopes)
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _service.CreateAsync(OwnerUserId, NewRequest(scopes: scopes)));

        (await _context.ApiKey.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public void EApiKeyScope_HasExactlyReadAndWrite()
    {
        // Guards the story decision "just read/write, no per-area scopes" — adding a
        // value here means the scope middleware in #236 has to grow a branch.
        Enum.GetValues<EApiKeyScope>().ShouldBe([EApiKeyScope.Read, EApiKeyScope.Write]);
    }

    // ─── Name & expiration validation ─────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAsync_EmptyName_Throws(string name)
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _service.CreateAsync(OwnerUserId, NewRequest(name)));
    }

    [Fact]
    public async Task CreateAsync_NameLongerThanTheColumn_Throws()
    {
        // Name is varchar(100) in the master schema. The API host would stop 101
        // characters at model validation, but the Functions host has none — there the
        // value would reach PostgreSQL and come back as 22001 → 500. Hence the guard
        // lives in the service, which both hosts go through.
        await Should.ThrowAsync<ArgumentException>(
            () => _service.CreateAsync(OwnerUserId, NewRequest(new string('x', 101))));

        (await _context.ApiKey.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task CreateAsync_NameExactlyAtTheColumnLimit_Succeeds()
    {
        // Boundary on the allowed side — the guard must reject 101, not 100.
        var name = new string('x', 100);

        var created = await _service.CreateAsync(OwnerUserId, NewRequest(name));

        created.Name.ShouldBe(name);
        (await LoadStoredKeyAsync(created.Id)).Name.ShouldBe(name);
    }

    [Fact]
    public async Task CreateAsync_TrimsName()
    {
        var created = await _service.CreateAsync(OwnerUserId, NewRequest("  Claude Desktop  "));

        created.Name.ShouldBe("Claude Desktop");
        (await LoadStoredKeyAsync(created.Id)).Name.ShouldBe("Claude Desktop");
    }

    [Fact]
    public async Task CreateAsync_PastExpiration_Throws()
    {
        var dto = NewRequest();
        dto.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);

        await Should.ThrowAsync<ArgumentException>(() => _service.CreateAsync(OwnerUserId, dto));
        (await _context.ApiKey.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task CreateAsync_FutureExpiration_IsStored()
    {
        var expiresAt = DateTime.UtcNow.AddDays(30);
        var dto = NewRequest();
        dto.ExpiresAt = expiresAt;

        var created = await _service.CreateAsync(OwnerUserId, dto);

        created.ExpiresAt.ShouldBe(expiresAt);
        (await LoadStoredKeyAsync(created.Id)).ExpiresAt.ShouldBe(expiresAt);
    }

    [Fact]
    public async Task CreateAsync_NoExpiration_MeansNeverExpires()
    {
        var created = await _service.CreateAsync(OwnerUserId, NewRequest());

        created.ExpiresAt.ShouldBeNull();
        created.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateAsync_SysAdminWithoutCompany_Succeeds()
    {
        // The key carries no CompanyId at all — the tenant is derived from the owner at
        // authentication time. That is what lets a SysAdmin key impersonate a tenant
        // later (#236) instead of being frozen to one company.
        var created = await _service.CreateAsync(SysAdminUserId, NewRequest("SysAdmin CLI"));

        created.Key.ShouldStartWith("fak_live_");
        typeof(ApiKey).GetProperty("CompanyId").ShouldBeNull();
    }

    // ─── Listing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAllAsync_ReturnsOnlyOwnKeys_NewestFirst()
    {
        var older = await _service.CreateAsync(OwnerUserId, NewRequest("older"));
        // CreatedAt is set from DateTime.UtcNow on save; force a distinguishable order.
        await ShiftCreatedAtAsync(older.Id, TimeSpan.FromMinutes(-5));
        var newer = await _service.CreateAsync(OwnerUserId, NewRequest("newer"));
        await _service.CreateAsync(OtherUserId, NewRequest("foreign"));

        var result = await _service.GetAllAsync(OwnerUserId);

        result.Select(k => k.Id).ShouldBe([newer.Id, older.Id]);
        result.ShouldAllBe(k => k.Name != "foreign");
    }

    [Fact]
    public async Task GetAllAsync_NeverExposesTheHash()
    {
        var created = await _service.CreateAsync(OwnerUserId, NewRequest());
        var stored = await LoadStoredKeyAsync(created.Id);

        var listed = (await _service.GetAllAsync(OwnerUserId)).Single();

        // The DTO has no hash property at all; this pins that it stays that way.
        typeof(ApiKeyDto).GetProperty("KeyHash").ShouldBeNull();
        listed.KeyPrefix.ShouldBe(stored.KeyPrefix);
        listed.KeyPrefix.ShouldNotBe(stored.KeyHash);
    }

    [Fact]
    public async Task GetAllAsync_NoKeys_ReturnsEmpty()
    {
        (await _service.GetAllAsync(OwnerUserId)).ShouldBeEmpty();
    }

    // ─── Revocation ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RevokeAsync_MarksKeyRevoked()
    {
        var created = await _service.CreateAsync(OwnerUserId, NewRequest());

        (await _service.RevokeAsync(OwnerUserId, created.Id)).ShouldBeTrue();

        var stored = await LoadStoredKeyAsync(created.Id);
        stored.RevokedAt.ShouldNotBeNull();
        stored.RevokedByUserId.ShouldBe(OwnerUserId);

        // Soft revoke — the audit row survives.
        (await _context.ApiKey.CountAsync()).ShouldBe(1);

        var listed = (await _service.GetAllAsync(OwnerUserId)).Single();
        listed.RevokedAt.ShouldNotBeNull();
        listed.IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task RevokeAsync_AlreadyRevoked_ReturnsFalseAndKeepsOriginalTimestamp()
    {
        var created = await _service.CreateAsync(OwnerUserId, NewRequest());
        await _service.RevokeAsync(OwnerUserId, created.Id);
        var firstRevokedAt = (await LoadStoredKeyAsync(created.Id)).RevokedAt;

        (await _service.RevokeAsync(OwnerUserId, created.Id)).ShouldBeFalse();

        (await LoadStoredKeyAsync(created.Id)).RevokedAt.ShouldBe(firstRevokedAt);
    }

    [Fact]
    public async Task RevokeAsync_OtherUsersKey_ReturnsFalseAndLeavesItActive()
    {
        var foreign = await _service.CreateAsync(OtherUserId, NewRequest("foreign"));

        (await _service.RevokeAsync(OwnerUserId, foreign.Id)).ShouldBeFalse();

        (await LoadStoredKeyAsync(foreign.Id)).RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task RevokeAsync_UnknownId_ReturnsFalse()
    {
        (await _service.RevokeAsync(OwnerUserId, 12345)).ShouldBeFalse();
    }

    // ─── DTO computed state ───────────────────────────────────────────────────

    [Fact]
    public void ApiKeyDto_IsActive_FalseWhenExpiredOrRevoked()
    {
        new ApiKeyDto().IsActive.ShouldBeTrue();
        new ApiKeyDto { ExpiresAt = DateTime.UtcNow.AddDays(1) }.IsActive.ShouldBeTrue();
        new ApiKeyDto { ExpiresAt = DateTime.UtcNow.AddSeconds(-1) }.IsActive.ShouldBeFalse();
        new ApiKeyDto { RevokedAt = DateTime.UtcNow }.IsActive.ShouldBeFalse();
    }

    /// <summary>
    /// Moves a stored key's CreatedAt by the given offset so ordering assertions do not
    /// depend on two saves landing in different ticks.
    /// Safe to save normally: the audit hook only rewrites CreatedAt for Added entities.
    /// </summary>
    private async Task ShiftCreatedAtAsync(long id, TimeSpan offset)
    {
        var entity = await _context.ApiKey.SingleAsync(k => k.Id == id);
        entity.CreatedAt = entity.CreatedAt.Add(offset);
        await _context.SaveChangesAsync();
    }
}
