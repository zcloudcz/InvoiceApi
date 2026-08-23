using AresService;
using Fakvio.Application.Service;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for the expiry sweep in <see cref="AresCacheRepository"/> — issue #200.
///
/// WHY THIS FILE EXISTS
///   AresCache rows were written but never deleted. Every distinct IČO that anyone ever
///   looked up left a permanent row behind, and since PR #173 the lookup is reachable
///   without a login (GET /api/auth/ares/{ico}), the number of keys is chosen by the
///   caller: enumerating the 8-digit space would grow the master database without bound.
///
///   The sweep runs on the write path, which is the only place rows are created, so the
///   table cannot grow while nothing is writing to it. Rows for IČOs that do not exist
///   expire after one hour (AresServiceImpl.FailureCacheExpiration), so the junk an
///   enumeration leaves behind is removed by the enumeration's own next request.
/// </summary>
public class AresCacheExpirySweepTests : IDisposable
{
    private readonly MasterDbContext _master;
    private readonly TenantDbContext _tenant;
    private readonly AresCacheRepository _sut;

    public AresCacheExpirySweepTests()
    {
        // No tenant is resolved (the default of the substitute is null), so the
        // repository works against MasterDbContext — the same path the anonymous
        // endpoint takes, which is the one being abused.
        var databaseName = Guid.NewGuid().ToString();

        _master = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(databaseName).Options);
        _tenant = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName + "-tenant").Options);

        _sut = new AresCacheRepository(
            _tenant,
            _master,
            Substitute.For<ITenantResolver>(),
            Substitute.For<ILogger<AresCacheRepository>>());
    }

    public void Dispose()
    {
        _master.Database.EnsureDeleted();
        _tenant.Database.EnsureDeleted();
        _master.Dispose();
        _tenant.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// THE BUG: nothing ever deleted an expired row. Writing a new entry now also
    /// removes rows whose TTL has passed.
    /// </summary>
    [Fact]
    public async Task SaveCache_RemovesExpiredRows()
    {
        Seed("11111111", expiresAt: DateTime.UtcNow.AddHours(-2));
        Seed("22222222", expiresAt: DateTime.UtcNow.AddMinutes(-1));
        await _master.SaveChangesAsync();

        await _sut.SaveCacheAsync(Entry("33333333", expiresAt: DateTime.UtcNow.AddDays(30)));

        var remaining = await _master.AresCache.Select(x => x.RegistrationNumber).ToListAsync();
        remaining.ShouldBe(["33333333"]);
    }

    /// <summary>
    /// The sweep must not touch rows that are still valid — they are the entire point
    /// of the cache (one fewer outbound call to the public registry).
    /// </summary>
    [Fact]
    public async Task SaveCache_KeepsRowsThatAreStillValid()
    {
        Seed("11111111", expiresAt: DateTime.UtcNow.AddDays(29));
        Seed("22222222", expiresAt: DateTime.UtcNow.AddHours(-2));
        await _master.SaveChangesAsync();

        await _sut.SaveCacheAsync(Entry("33333333", expiresAt: DateTime.UtcNow.AddDays(30)));

        var remaining = await _master.AresCache
            .Select(x => x.RegistrationNumber).OrderBy(x => x).ToListAsync();
        remaining.ShouldBe(["11111111", "33333333"]);
    }

    /// <summary>
    /// Refreshing an entry that has just expired is the most common write of all
    /// (cache miss on an existing key). The sweep must not delete the very row the
    /// caller is refreshing — that would drop the fresh data on the floor.
    /// </summary>
    [Fact]
    public async Task SaveCache_RefreshingAnExpiredRow_KeepsTheRefreshedRow()
    {
        Seed("11111111", expiresAt: DateTime.UtcNow.AddHours(-2), companyName: "Stará firma");
        await _master.SaveChangesAsync();

        await _sut.SaveCacheAsync(Entry(
            "11111111", expiresAt: DateTime.UtcNow.AddDays(30), companyName: "Nová firma"));

        var rows = await _master.AresCache.ToListAsync();
        rows.Count.ShouldBe(1);
        rows[0].CompanyName.ShouldBe("Nová firma");
    }

    /// <summary>
    /// THE BUG (found in review of PR #246): the sweep shared one SaveChanges with the cache
    /// write. Two requests that pick the same expired batch race each other — the loser's
    /// DELETE matches zero rows, EF reports DbUpdateConcurrencyException, and because the
    /// new cache row was in the same save, the loser lost its own insert too. Nothing is
    /// visible from outside: AresServiceImpl.CacheResult swallows it as a Warning, so a
    /// burst of lookups would quietly stop caching.
    ///
    /// The failing context stands in for the lost race: it rejects any save that carries
    /// deletes, which is exactly what the database does to the loser.
    /// </summary>
    [Fact]
    public async Task SaveCache_SweepLosesTheConcurrencyRace_StillKeepsTheNewEntry()
    {
        var databaseName = Guid.NewGuid().ToString();
        using var master = new SweepLosesTheRaceDbContext(
            new DbContextOptionsBuilder<MasterDbContext>().UseInMemoryDatabase(databaseName).Options);
        using var tenant = new TenantDbContext(
            new DbContextOptionsBuilder<TenantDbContext>().UseInMemoryDatabase(databaseName + "-tenant").Options);

        var sut = new AresCacheRepository(
            tenant, master,
            Substitute.For<ITenantResolver>(),
            Substitute.For<ILogger<AresCacheRepository>>());

        master.AresCache.Add(Row("11111111", expiresAt: DateTime.UtcNow.AddHours(-2)));
        await master.SaveChangesAsync(); // no deletes yet, so this save is allowed through

        await Should.NotThrowAsync(() => sut.SaveCacheAsync(Entry("33333333", DateTime.UtcNow.AddDays(30))));

        master.RejectedSaves.ShouldBe(1, "the sweep has to be a save of its own, separate from the insert");

        var remaining = await master.AresCache.Select(x => x.RegistrationNumber).ToListAsync();
        remaining.ShouldContain("33333333", "a lost sweep race must not cost the caller its cache entry");

        master.ChangeTracker.Entries().Any(e => e.State == EntityState.Deleted)
            .ShouldBeFalse("rows left in Deleted state would fail every later save in the same request scope");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void Seed(string registrationNumber, DateTime expiresAt, string? companyName = null)
        => _master.AresCache.Add(Row(registrationNumber, expiresAt, companyName));

    private static Fakvio.Domain.Entities.AresCache Row(
        string registrationNumber, DateTime expiresAt, string? companyName = null) => new()
        {
            RegistrationNumber = registrationNumber,
            JsonData = "{}",
            FetchedAt = DateTime.UtcNow.AddDays(-30),
            ExpiresAt = expiresAt,
            IsSuccessful = true,
            CompanyName = companyName
        };

    /// <summary>
    /// MasterDbContext that refuses any save carrying deleted rows — the deterministic
    /// stand-in for losing the sweep race against a concurrent writer (the real database
    /// answers a DELETE that matches nothing with DbUpdateConcurrencyException).
    /// </summary>
    private sealed class SweepLosesTheRaceDbContext(DbContextOptions<MasterDbContext> options)
        : MasterDbContext(options)
    {
        public int RejectedSaves { get; private set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (ChangeTracker.Entries().Any(e => e.State == EntityState.Deleted))
            {
                RejectedSaves++;
                throw new DbUpdateConcurrencyException("another writer already deleted these rows");
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }

    private static AresCacheEntry Entry(
        string registrationNumber, DateTime expiresAt, string? companyName = null) => new()
        {
            RegistrationNumber = registrationNumber,
            JsonData = "{}",
            FetchedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
            IsSuccessful = true,
            CompanyName = companyName
        };
}
