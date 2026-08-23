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

    /// <summary>
    /// The sweep is capped (ExpiredSweepBatchSize = 200) so that one lookup never pays for
    /// a whole backlog left by an earlier burst. Without the cap a single write could turn
    /// into a DELETE over the entire table — the exact opposite of what an anonymous,
    /// enumerable endpoint should be able to trigger.
    ///
    /// The backlog still drains: every write removes a batch and adds one row.
    /// </summary>
    [Fact]
    public async Task SaveCache_MoreExpiredRowsThanTheBatch_RemovesOneBatchAndLeavesTheRest()
    {
        const int expiredRowCount = SweepBatchSize + 1;

        for (var i = 0; i < expiredRowCount; i++)
            Seed(RegistrationNumberOf(i), expiresAt: DateTime.UtcNow.AddHours(-2));
        await _master.SaveChangesAsync();

        await _sut.SaveCacheAsync(Entry("99999999", expiresAt: DateTime.UtcNow.AddDays(30)));

        var remaining = await _master.AresCache.CountAsync();
        remaining.ShouldBe(2, "one expired row above the batch size, plus the row just written");
    }

    /// <summary>
    /// The repository picks its context from the tenant resolver. A signed-in user sweeps
    /// their own tenant schema; the master table is not theirs to clean (and vice versa —
    /// the anonymous path in the tests above sweeps master only).
    /// </summary>
    [Fact]
    public async Task SaveCache_WithTenantResolved_SweepsTheTenantTableAndLeavesMasterAlone()
    {
        var tenantResolver = Substitute.For<ITenantResolver>();
        tenantResolver.GetCurrentCompanyId().Returns(SignedInCompanyId);

        var sut = new AresCacheRepository(
            _tenant, _master, tenantResolver, Substitute.For<ILogger<AresCacheRepository>>());

        _tenant.AresCache.Add(Row("11111111", expiresAt: DateTime.UtcNow.AddHours(-2)));
        _master.AresCache.Add(Row("22222222", expiresAt: DateTime.UtcNow.AddHours(-2)));
        await _tenant.SaveChangesAsync();
        await _master.SaveChangesAsync();

        await sut.SaveCacheAsync(Entry("33333333", expiresAt: DateTime.UtcNow.AddDays(30)));

        var tenantRows = await _tenant.AresCache.Select(x => x.RegistrationNumber).ToListAsync();
        tenantRows.ShouldBe(["33333333"], "the expired tenant row is swept, the new one stays");

        var masterRows = await _master.AresCache.Select(x => x.RegistrationNumber).ToListAsync();
        masterRows.ShouldBe(["22222222"], "master belongs to a different context and is left alone");
    }

    // ── Characterized gap: a sweep failure that is not a lost race ────────────

    /// <summary>
    /// CHARACTERIZATION, not an endorsement (reviewer note on PR #246, AresCacheRepository:212).
    ///
    /// The catch only handles DbUpdateConcurrencyException. Any other save failure — a
    /// deadlock victim, a statement timeout — escapes with the swept rows still marked
    /// Deleted in the change tracker. That tracker belongs to the scoped MasterDbContext,
    /// which is shared for the rest of the request: registration (AuthService.RegisterAsync)
    /// does an ARES lookup and then saves the new Client through the same context, so it
    /// would carry the failed sweep's deletes into its own save and fail with it.
    ///
    /// This test pins the CURRENT behaviour, so the two-line follow-up
    /// (catch when (ex is not OperationCanceledException) + always detach) has a red test
    /// waiting: when it lands, both assertions below flip to "does not throw" / "no
    /// Deleted entries", exactly like the DbUpdateConcurrencyException test above.
    /// </summary>
    [Fact]
    public async Task SaveCache_SweepFailsWithSomethingOtherThanALostRace_LeavesDeletesInTheSharedContext()
    {
        var databaseName = Guid.NewGuid().ToString();
        using var master = new SweepTimesOutDbContext(
            new DbContextOptionsBuilder<MasterDbContext>().UseInMemoryDatabase(databaseName).Options);
        using var tenant = new TenantDbContext(
            new DbContextOptionsBuilder<TenantDbContext>().UseInMemoryDatabase(databaseName + "-tenant").Options);

        var sut = new AresCacheRepository(
            tenant, master,
            Substitute.For<ITenantResolver>(),
            Substitute.For<ILogger<AresCacheRepository>>());

        master.AresCache.Add(Row("11111111", expiresAt: DateTime.UtcNow.AddHours(-2)));
        await master.SaveChangesAsync(); // no deletes yet, so this save is allowed through

        var thrown = await Should.ThrowAsync<DbUpdateException>(
            () => sut.SaveCacheAsync(Entry("33333333", DateTime.UtcNow.AddDays(30))));
        thrown.ShouldNotBeOfType<DbUpdateConcurrencyException>(
            "this is the failure mode the catch does NOT handle");

        master.ChangeTracker.Entries().Count(e => e.State == EntityState.Deleted)
            .ShouldBe(1, "CURRENT behaviour — the follow-up fix detaches here as well");

        // What that leftover costs the request: the next save on the same scoped context —
        // in production the registration save at AuthService.RegisterAsync — replays the
        // failing delete and goes down with it.
        await Should.ThrowAsync<DbUpdateException>(() => master.SaveChangesAsync());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Mirrors AresCacheRepository.ExpiredSweepBatchSize, which is private. If the
    /// production constant changes, the batch test above fails and points here.
    /// </summary>
    private const int SweepBatchSize = 200;

    /// <summary>Any company id turns the repository from the master path to the tenant path.</summary>
    private const long SignedInCompanyId = 42;

    /// <summary>Distinct 8-digit IČOs for bulk seeding: 0 → "10000000", 1 → "10000001", …</summary>
    private static string RegistrationNumberOf(int index) => (10_000_000 + index).ToString();


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

    /// <summary>
    /// MasterDbContext that fails any save carrying deleted rows with a plain
    /// DbUpdateException — the stand-in for a deadlock victim or a statement timeout,
    /// i.e. a sweep failure that is NOT the lost race the repository handles.
    /// </summary>
    private sealed class SweepTimesOutDbContext(DbContextOptions<MasterDbContext> options)
        : MasterDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => ChangeTracker.Entries().Any(e => e.State == EntityState.Deleted)
                ? throw new DbUpdateException("deadlock detected while deleting expired rows")
                : base.SaveChangesAsync(cancellationToken);
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
