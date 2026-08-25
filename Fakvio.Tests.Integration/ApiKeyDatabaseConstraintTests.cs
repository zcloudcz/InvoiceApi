using System.Net.Sockets;
using Fakvio.Contracts.Dto.ApiKey;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Authentication;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using ApiKeyEntity = Fakvio.Domain.Entities.ApiKey;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Integration tests for issues #235 and #236 — the <c>ApiKey</c> table against a REAL
/// PostgreSQL schema instead of an InMemory store.
///
/// Why the real database matters here: <c>Fakvio.Tests.Unit/ApiKeyServiceTests</c> runs on
/// InMemory, which has no unique indexes, no column length limits and no foreign keys. It
/// can show that <c>ApiKeyService</c> builds the right rows, but it physically cannot show
/// that the rows are constrained the way the security design assumes they are:
///
/// * <c>IX_ApiKey_KeyHash</c> unique — the collision guard behind "one hash = one key".
///   On InMemory two identical hashes insert happily.
/// * <c>Name varchar(100)</c> — the reason <c>CreateAsync</c> validates the length itself.
///   On InMemory an over-long name is stored as-is, so the guard looks decorative.
/// * <c>FK_ApiKey_User_UserId ON DELETE CASCADE</c> — the reason keys need no separate
///   cleanup when a user is removed. On InMemory nothing cascades.
/// * <c>ExpiresAt</c> comes back from a <c>timestamp with time zone</c> column with a
///   DateTimeKind the code must not assume (#236). InMemory round-trips the kind verbatim
///   and therefore cannot show the mismatch the expiry check has to survive.
///
/// The schema is built by running the real master migrations (including
/// <c>AddApiKey_v147</c>) into a throwaway schema, so what the tests hit is the DDL that
/// production will actually apply — not a model snapshot that happens to agree with it.
///
/// Each test class instance creates its own throwaway schema and drops it afterwards, so
/// the tests never collide with the developer's dev data or with each other.
///
/// The tests are skipped (not failed) when PostgreSQL is unreachable — the local dev
/// database is started with <c>docker compose up -d</c>. Override the connection with
/// the <c>FAKVIO_TEST_POSTGRES</c> environment variable.
/// </summary>
// Shares one xUnit collection with the other real-schema test classes so they never issue
// DDL against the same PostgreSQL concurrently — see RealPostgreSqlCollection.
[Collection(RealPostgreSqlCollection.Name)]
public class ApiKeyDatabaseConstraintTests : IAsyncLifetime
{
    /// <summary>Matches docker-compose.yml — the local dev PostgreSQL.</summary>
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=fakvio;Username=fakvio;Password=fakvio_dev;Timeout=5";

    private const string ConnectionStringEnvVar = "FAKVIO_TEST_POSTGRES";

    private const string SkipReason =
        "PostgreSQL is not reachable — start it with 'docker compose up -d' " +
        "or point " + ConnectionStringEnvVar + " at another instance.";

    /// <summary>Owner of the keys in every test. Named after the issue to stay out of seed range.</summary>
    private const long OwnerUserId = 235_001L;

    /// <summary>The <c>Name</c> column limit from the migration — and from CreateApiKeyDto.</summary>
    private const int NameColumnLimit = 100;

    /// <summary>Canonical scope string as <c>ApiKeyService.NormalizeScopes</c> produces it.</summary>
    private const string ReadScope = "read";

    private readonly string _schemaName = $"test_issue235_{Guid.NewGuid():N}"[..40];
    private readonly string _connectionString =
        Environment.GetEnvironmentVariable(ConnectionStringEnvVar) ?? DefaultConnectionString;

    private NpgsqlDataSourceFactory? _dataSourceFactory;
    private bool _databaseAvailable;

    // ─── Fixture lifetime ─────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        // The real factory in password mode — the same object graph production builds, so
        // the per-schema search_path handling under test is the production one.
        _dataSourceFactory = new NpgsqlDataSourceFactory(new DatabaseOptions
        {
            ConnectionString = _connectionString,
            AuthMode = DatabaseAuthMode.Password
        });

        _databaseAvailable = await CanReachPostgreSqlAsync();

        if (!_databaseAvailable)
            return; // Every test skips with SkipReason.

        // Deliberately NOT wrapped in a try/catch: the reachability probe above is the only
        // reason this class is allowed to skip. Once the server answers, a failure to build
        // the schema is a real defect and must fail loudly instead of turning into a
        // silently-green skip.
        await using (var createSchema = _dataSourceFactory.Root.CreateCommand(
                         $"CREATE SCHEMA \"{_schemaName}\""))
        {
            await createSchema.ExecuteNonQueryAsync();
        }

        // MigrateAsync — not EnsureCreated/CreateTables — because the point is to prove the
        // migration itself lands cleanly on an empty schema. EnsureCreated would build the
        // tables from the model and could stay green while AddApiKey_v147 was broken.
        await using var context = CreateMasterContext();
        await context.Database.MigrateAsync();

        await SeedOwnerAsync();
    }

    /// <summary>
    /// Opens and immediately closes one raw connection. EF wraps connection failures in a
    /// generic "transient failure" InvalidOperationException, which is indistinguishable
    /// from a genuine bug — so the reachability question is answered here, at the driver
    /// level, where the exception types are unambiguous.
    /// </summary>
    private async Task<bool> CanReachPostgreSqlAsync()
    {
        try
        {
            await using var connection = await _dataSourceFactory!.Root.OpenConnectionAsync();
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or SocketException)
        {
            return false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_databaseAvailable && _dataSourceFactory is not null)
        {
            await using var command = _dataSourceFactory.Root.CreateCommand(
                $"DROP SCHEMA IF EXISTS \"{_schemaName}\" CASCADE");
            await command.ExecuteNonQueryAsync();
        }

        if (_dataSourceFactory is not null)
            await _dataSourceFactory.DisposeAsync();
    }

    /// <summary>
    /// Builds a MasterDbContext bound to this test's throwaway schema.
    ///
    /// <c>includePublicInSearchPath: false</c> is load-bearing: the dev database's own master
    /// tables — including <c>__EFMigrationsHistory</c> — live in <c>public</c>. With public on
    /// the search path EF would read that history, conclude every migration is already
    /// applied, and leave the throwaway schema empty.
    ///
    /// PendingModelChangesWarning is downgraded exactly as
    /// <c>ServiceCollectionExtensions.AddDatabaseContexts</c> does it for the production
    /// MasterDbContext. Without it this class is order-dependent: the API host that the
    /// WebApplicationFactory-based tests boot sets the process-wide
    /// <c>Npgsql.EnableLegacyTimestampBehavior</c> switch, which changes the store type
    /// DateTime maps to — so a model built after one of those classes ran no longer matches
    /// the migration snapshot and MigrateAsync throws. Production suppresses the same
    /// warning for the same MigrateAsync call, so suppressing it here keeps the test on the
    /// production code path rather than papering over a defect.
    ///
    /// A fresh context per call means assertions read from the database, not from a change
    /// tracker that still holds the objects the arrange step just wrote.
    /// </summary>
    private MasterDbContext CreateMasterContext()
        => new(new DbContextOptionsBuilder<MasterDbContext>()
            .UseNpgsql(_dataSourceFactory!.GetForSchema(_schemaName, includePublicInSearchPath: false))
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options);

    // ─── Arrange helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// The user every key in this class belongs to. Keys are FK-bound to a user, so there is
    /// no such thing as an ApiKey test without one.
    /// </summary>
    private async Task SeedOwnerAsync()
    {
        await using var context = CreateMasterContext();

        context.User.Add(new User
        {
            Id = OwnerUserId,
            Email = "api-key-owner@fakvio.test",
            // Not a login path in these tests — the hash only has to be non-null.
            PasswordHash = "not-a-real-hash",
            FirstName = "Api",
            LastName = "Owner",
            Role = EUserRole.User,
            IsActive = true,
            IsEmailVerified = true,
            ExternalProvider = EExternalProvider.None
        });

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// A valid ApiKey row for the seeded owner. Callers override only the field their test
    /// is about, so a test's arrange block shows exactly what makes it different.
    /// </summary>
    private static ApiKeyEntity BuildApiKey(string keyHash, string name = "Test key")
        => new()
        {
            UserId = OwnerUserId,
            Name = name,
            KeyPrefix = "fak_live_abc",
            KeyHash = keyHash,
            Scopes = ReadScope
        };

    /// <summary>Inserts a row straight through EF, bypassing the service's own validation.</summary>
    private async Task InsertApiKeyAsync(ApiKeyEntity apiKey)
    {
        await using var context = CreateMasterContext();
        context.ApiKey.Add(apiKey);
        await context.SaveChangesAsync();
    }

    /// <summary>The production service over this test's schema, as the API resolves it per request.</summary>
    private ApiKeyService CreateApiKeyService(MasterDbContext context)
        => new(context, NullLogger<ApiKeyService>.Instance);

    // ─── Tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The migration ran, and it produced the two indexes the design depends on — the unique
    /// one for authentication lookups, the plain one for listing a user's keys. Reading them
    /// back from the catalog proves the DDL, not the model snapshot.
    /// </summary>
    [SkippableFact]
    public async Task Migration_CreatesApiKeyIndexes_WithKeyHashUnique()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var indexes = await ReadApiKeyIndexUniquenessAsync();

        indexes["IX_ApiKey_KeyHash"].ShouldBeTrue("KeyHash must be UNIQUE — it is the authentication selector.");
        indexes["IX_ApiKey_UserId"].ShouldBeFalse("A user owns many keys, so this index must not be unique.");
    }

    /// <summary>
    /// The collision guard. Two rows carrying the same SHA-256 would make authentication
    /// ambiguous — one raw key resolving to two identities. InMemory allows it; PostgreSQL
    /// must not.
    /// </summary>
    [SkippableFact]
    public async Task SecondKeyWithTheSameHash_ViolatesUniqueIndex()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        const string sharedHash = "b3JlLWFuZC10aGUtc2FtZS1oYXNoLWZvci1ib3RoLXJvd3M=";
        await InsertApiKeyAsync(BuildApiKey(sharedHash, "First key"));

        var act = () => InsertApiKeyAsync(BuildApiKey(sharedHash, "Duplicate key"));

        var exception = await Should.ThrowAsync<DbUpdateException>(act);
        var postgresException = exception.InnerException.ShouldBeOfType<PostgresException>();
        postgresException.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        postgresException.ConstraintName.ShouldBe("IX_ApiKey_KeyHash");
    }

    /// <summary>
    /// Why <c>CreateAsync</c> checks the name length itself: PostgreSQL does not truncate an
    /// over-long value into varchar(100), it aborts the statement with 22001. Without the
    /// guard that is a 500 on the Functions host, which has no model validation in front of it.
    /// </summary>
    [SkippableFact]
    public async Task NameLongerThanColumnLimit_IsRejectedByPostgreSql_NotTruncated()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var overLongName = new string('n', NameColumnLimit + 1);

        var act = () => InsertApiKeyAsync(BuildApiKey("aG93LWxvbmctY2FuLWEtbmFtZS1nZXQ=", overLongName));

        var exception = await Should.ThrowAsync<DbUpdateException>(act);
        exception.InnerException.ShouldBeOfType<PostgresException>()
            .SqlState.ShouldBe(PostgresErrorCodes.StringDataRightTruncation);
    }

    /// <summary>
    /// The other half of the previous test: the service stops the same value before it ever
    /// reaches the column, so a client gets a 400 instead of the 22001 above — and nothing
    /// is written.
    /// </summary>
    [SkippableFact]
    public async Task CreateAsync_WithNameOverColumnLimit_FailsBeforeReachingDatabase()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        await using var context = CreateMasterContext();
        var service = CreateApiKeyService(context);
        var dto = new CreateApiKeyDto
        {
            Name = new string('n', NameColumnLimit + 1),
            Scopes = ReadScope
        };

        var act = () => service.CreateAsync(OwnerUserId, dto);

        await Should.ThrowAsync<ArgumentException>(act);

        await using var readContext = CreateMasterContext();
        (await readContext.ApiKey.CountAsync()).ShouldBe(0);
    }

    /// <summary>
    /// A name exactly at the limit is the boundary on the legal side — the guard must reject
    /// 101 characters without also rejecting 100, and the column must accept what it allows.
    /// </summary>
    [SkippableFact]
    public async Task CreateAsync_WithNameExactlyAtColumnLimit_Persists()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var maximumLengthName = new string('n', NameColumnLimit);

        await using (var context = CreateMasterContext())
        {
            await CreateApiKeyService(context)
                .CreateAsync(OwnerUserId, new CreateApiKeyDto { Name = maximumLengthName, Scopes = ReadScope });
        }

        await using var readContext = CreateMasterContext();
        var stored = await readContext.ApiKey.SingleAsync();
        stored.Name.ShouldBe(maximumLengthName);
    }

    /// <summary>
    /// The security invariant of the whole feature: the raw key is returned once and never
    /// stored. Against a real column this also proves the hash survives a round trip
    /// unchanged — Base64 in varchar(64) with no encoding surprises.
    /// </summary>
    [SkippableFact]
    public async Task CreateAsync_StoresOnlyTheHash_NeverTheRawKey()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        CreatedApiKeyDto created;
        await using (var context = CreateMasterContext())
        {
            created = await CreateApiKeyService(context)
                .CreateAsync(OwnerUserId, new CreateApiKeyDto { Name = "Round trip", Scopes = ReadScope });
        }

        await using var readContext = CreateMasterContext();
        var stored = await readContext.ApiKey.SingleAsync();

        stored.KeyHash.ShouldBe(ApiKeyService.ComputeHash(created.Key));
        stored.KeyHash.ShouldNotBe(created.Key);
        stored.KeyPrefix.ShouldBe(created.Key[..stored.KeyPrefix.Length]);
    }

    /// <summary>
    /// Cascade delete is why the entity carries no cleanup logic: removing the user removes
    /// the credentials, in one statement, with no window in which an orphaned key still
    /// authenticates. Nothing in the C# code does this — only the FK does.
    /// </summary>
    [SkippableFact]
    public async Task DeletingOwner_CascadeDeletesTheirApiKeys()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        await InsertApiKeyAsync(BuildApiKey("Y2FzY2FkZS1tZS1hd2F5LXBsZWFzZS10aGFua3M="));

        await using (var deleteContext = CreateMasterContext())
        {
            var owner = await deleteContext.User.SingleAsync(u => u.Id == OwnerUserId);
            deleteContext.User.Remove(owner);
            await deleteContext.SaveChangesAsync();
        }

        await using var readContext = CreateMasterContext();
        (await readContext.ApiKey.CountAsync(k => k.UserId == OwnerUserId)).ShouldBe(0);
    }

    /// <summary>
    /// A key belonging to another user must not be deletable through the FK either — the
    /// cascade is scoped to the owner, not to "any user row disappearing".
    /// </summary>
    [SkippableFact]
    public async Task DeletingAnotherUser_LeavesTheOwnersApiKeysIntact()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        const long unrelatedUserId = OwnerUserId + 1;
        await InsertApiKeyAsync(BuildApiKey("c3RheS1wdXQtd2hlbi1zb21lb25lLWVsc2UtZ29lcw=="));

        await using (var context = CreateMasterContext())
        {
            context.User.Add(new User
            {
                Id = unrelatedUserId,
                Email = "unrelated@fakvio.test",
                PasswordHash = "not-a-real-hash",
                FirstName = "Unrelated",
                LastName = "User",
                Role = EUserRole.User,
                IsActive = true,
                ExternalProvider = EExternalProvider.None
            });
            await context.SaveChangesAsync();

            context.User.Remove(await context.User.SingleAsync(u => u.Id == unrelatedUserId));
            await context.SaveChangesAsync();
        }

        await using var readContext = CreateMasterContext();
        (await readContext.ApiKey.CountAsync(k => k.UserId == OwnerUserId)).ShouldBe(1);
    }

    /// <summary>
    /// A key for a user that does not exist must not be insertable. This is the guarantee
    /// that makes "the key's tenant is its owner's tenant" safe — there is always an owner.
    /// </summary>
    [SkippableFact]
    public async Task KeyForUnknownUser_ViolatesForeignKey()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var orphan = BuildApiKey("bm8tc3VjaC11c2VyLWV4aXN0cy1pbi10aGlzLXNjaGVtYQ==");
        orphan.UserId = OwnerUserId + 999;

        var act = () => InsertApiKeyAsync(orphan);

        var exception = await Should.ThrowAsync<DbUpdateException>(act);
        exception.InnerException.ShouldBeOfType<PostgresException>()
            .SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
    }

    /// <summary>
    /// Revocation is monotonic: the second call is a no-op that reports "nothing to do",
    /// and — the part InMemory cannot vouch for — the persisted timestamp from the first
    /// call is not overwritten by it.
    /// </summary>
    [SkippableFact]
    public async Task RevokeAsync_CalledTwice_KeepsTheFirstRevocation()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        long keyId;
        await using (var context = CreateMasterContext())
        {
            var created = await CreateApiKeyService(context)
                .CreateAsync(OwnerUserId, new CreateApiKeyDto { Name = "Revoke twice", Scopes = ReadScope });
            keyId = created.Id;
        }

        bool firstResult, secondResult;
        DateTime? revokedAtAfterFirstCall;

        await using (var firstCall = CreateMasterContext())
        {
            firstResult = await CreateApiKeyService(firstCall).RevokeAsync(OwnerUserId, keyId);
        }

        await using (var afterFirst = CreateMasterContext())
        {
            revokedAtAfterFirstCall = (await afterFirst.ApiKey.SingleAsync()).RevokedAt;
        }

        await using (var secondCall = CreateMasterContext())
        {
            secondResult = await CreateApiKeyService(secondCall).RevokeAsync(OwnerUserId, keyId);
        }

        firstResult.ShouldBeTrue();
        secondResult.ShouldBeFalse();

        await using var readContext = CreateMasterContext();
        var stored = await readContext.ApiKey.SingleAsync();
        stored.RevokedAt.ShouldBe(revokedAtAfterFirstCall);
        stored.RevokedByUserId.ShouldBe(OwnerUserId);
    }

    /// <summary>
    /// Revoking someone else's key must be indistinguishable from revoking a key that does
    /// not exist — and must leave the row untouched in the database, not merely return false.
    /// </summary>
    [SkippableFact]
    public async Task RevokeAsync_ByNonOwner_LeavesTheKeyActive()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        long keyId;
        await using (var context = CreateMasterContext())
        {
            var created = await CreateApiKeyService(context)
                .CreateAsync(OwnerUserId, new CreateApiKeyDto { Name = "Not yours", Scopes = ReadScope });
            keyId = created.Id;
        }

        bool result;
        await using (var attacker = CreateMasterContext())
        {
            result = await CreateApiKeyService(attacker).RevokeAsync(OwnerUserId + 1, keyId);
        }

        result.ShouldBeFalse();

        await using var readContext = CreateMasterContext();
        (await readContext.ApiKey.SingleAsync()).RevokedAt.ShouldBeNull();
    }

    // ─── Expiry across the driver boundary (issue #236) ───────────────────────

    /// <summary>
    /// Pins the fact the expiry check is built on: with
    /// <c>Npgsql.EnableLegacyTimestampBehavior</c> — switched on process-wide by both hosts —
    /// a <c>timestamp with time zone</c> column does NOT come back as a UTC DateTime. It
    /// comes back converted to the server's local time with <see cref="DateTimeKind.Local"/>.
    ///
    /// Nothing in an InMemory test can show this, and it is the whole reason
    /// <c>ApiKeyAuthenticator</c> normalizes before comparing: a raw
    /// <c>ExpiresAt &lt;= DateTime.UtcNow</c> would be wrong by the local UTC offset, and east
    /// of Greenwich wrong in the dangerous direction — an expired key would keep working.
    /// </summary>
    [SkippableFact]
    public async Task ExpiresAt_ComesBackFromPostgres_WithTheKindTheLegacySwitchDictates()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var key = BuildApiKey("kind-round-trip-hash");
        key.ExpiresAt = DateTime.UtcNow.AddHours(1);
        await InsertApiKeyAsync(key);

        await using var context = CreateMasterContext();
        var stored = await context.ApiKey.AsNoTracking().SingleAsync(k => k.KeyHash == "kind-round-trip-hash");

        // Production always takes the Local branch — both hosts set the switch at startup.
        // Which branch THIS process is in depends on whether a WebApplicationFactory-based
        // class already booted the API host (see CreateMasterContext), so read the switch
        // instead of pretending the answer is fixed. Either way the point stands: the kind
        // that comes back is not something the expiry comparison may assume.
        AppContext.TryGetSwitch("Npgsql.EnableLegacyTimestampBehavior", out var legacyTimestamps);
        stored.ExpiresAt!.Value.Kind.ShouldBe(legacyTimestamps ? DateTimeKind.Local : DateTimeKind.Utc);
    }

    /// <summary>
    /// The security assertion behind the test above: an expired key does not authenticate,
    /// against a real PostgreSQL round-trip. On a machine east of UTC this test fails
    /// outright if the comparison stops normalizing the kind.
    /// </summary>
    [SkippableFact]
    public async Task ExpiredKey_IsRejected_AfterARealPostgresRoundTrip()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var rawKey = "fak_live_" + new string('E', 43);
        var key = BuildApiKey(ApiKeyService.ComputeHash(rawKey), name: "Expired key");
        key.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await InsertApiKeyAsync(key);

        await using var context = CreateMasterContext();
        var authenticator = new ApiKeyAuthenticator(context, NullLogger<ApiKeyAuthenticator>.Instance);

        (await authenticator.AuthenticateAsync(rawKey)).ShouldBeNull();
    }

    /// <summary>
    /// Boundary companion: normalizing the kind must not retire a live key early either.
    /// West of UTC an un-normalized comparison fails in this direction instead.
    /// </summary>
    [SkippableFact]
    public async Task LiveKey_IsAccepted_AfterARealPostgresRoundTrip()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var rawKey = "fak_live_" + new string('L', 43);
        var key = BuildApiKey(ApiKeyService.ComputeHash(rawKey), name: "Live key");
        key.ExpiresAt = DateTime.UtcNow.AddMinutes(1);
        await InsertApiKeyAsync(key);

        await using var context = CreateMasterContext();
        var authenticator = new ApiKeyAuthenticator(context, NullLogger<ApiKeyAuthenticator>.Instance);

        (await authenticator.AuthenticateAsync(rawKey)).ShouldNotBeNull();
    }

    // ─── Catalog helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Reads index name → is-unique for the ApiKey table in this test's schema, straight
    /// from the PostgreSQL catalog. Asking the catalog rather than EF's model is the point:
    /// the model is what the migration was generated from, the catalog is what it produced.
    /// </summary>
    private async Task<Dictionary<string, bool>> ReadApiKeyIndexUniquenessAsync()
    {
        const string catalogQuery = """
            SELECT i.relname, ix.indisunique
            FROM pg_index ix
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN pg_class t ON t.oid = ix.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE t.relname = 'ApiKey' AND n.nspname = @schema
            """;

        await using var command = _dataSourceFactory!.Root.CreateCommand(catalogQuery);
        command.Parameters.AddWithValue("schema", _schemaName);

        var indexes = new Dictionary<string, bool>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            indexes[reader.GetString(0)] = reader.GetBoolean(1);

        return indexes;
    }
}
