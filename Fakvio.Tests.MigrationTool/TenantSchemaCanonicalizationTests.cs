using System.Net.Sockets;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Fakvio.MigrationTool;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;

namespace Fakvio.Tests.MigrationTool;

/// <summary>
/// Drives <see cref="DataMigrationService"/> — the whole migration tool — against a REAL
/// PostgreSQL server and pins the one invariant its composition root has to hold:
/// <b>the tenant schema name written into <c>CompanySystemSettings.SchemaName</c> must name
/// the schema the tool actually created.</b>
///
/// Why this needs a live database (issue #136, PR #166 review round 1): the physical schema
/// is created through <see cref="SchemaNames.Sanitize"/>, which lowercases. If the persisted
/// name were composed separately from the raw <c>Migration:TenantSchemaPrefix</c>, a
/// non-canonical prefix such as <c>"Tenant_"</c> would create <c>tenant_42</c> but store
/// <c>Tenant_42</c>. The runtime <c>TenantDbContextFactory</c> uses the stored name verbatim,
/// so the freshly migrated tenant would be unreachable — silently.
/// <c>DataIntegrityVerifier</c> cannot catch it either: it sanitizes the stored name before
/// looking the schema up, finds the data and reports PASS.
///
/// Why the existing unit tests do not cover it (review round 2, measured): reverting the fix
/// in <c>DataMigrationService.MigrateCompanyAsync</c> left <c>SchemaNamesTests</c> green,
/// because no test project referenced <c>Fakvio.MigrationTool</c> at all. Those tests document
/// <see cref="SchemaNames.Sanitize"/>; only a test that runs the real call site can pin the
/// composition. Hence this project and this class.
///
/// Isolation: each test-class instance owns a GUID and three throwaway schemas derived from it
/// (source, master, tenant). <c>DisposeAsync</c> drops every schema whose name contains that
/// GUID, so a run never touches developer data and two runs never collide. The tenant prefix
/// is GUID-scoped as well — a bare <c>"Tenant_"</c> would compose <c>tenant_1</c> and collide
/// with a real tenant schema.
///
/// Skipped (not failed) when PostgreSQL is unreachable — start it with
/// <c>docker compose up -d</c>, or point <c>FAKVIO_TEST_POSTGRES</c> elsewhere.
/// See DEVGUIDE §8.2.1 for the shared throwaway-schema pattern.
/// </summary>
public class TenantSchemaCanonicalizationTests : IAsyncLifetime
{
    /// <summary>Matches docker-compose.yml — the local dev PostgreSQL.</summary>
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=fakvio;Username=fakvio;Password=fakvio_dev;Timeout=5";

    private const string ConnectionStringEnvVar = "FAKVIO_TEST_POSTGRES";

    private const string SkipReason =
        "PostgreSQL is not reachable — start it with 'docker compose up -d' " +
        "or point " + ConnectionStringEnvVar + " at another instance.";

    /// <summary>The single legacy company the migration has to move. The values are arbitrary.</summary>
    private const string SourceCompanyName = "Migrated Legacy s.r.o.";
    private const string SourceRegistrationNumber = "12345678";

    /// <summary>A table every provisioned tenant schema must own — used as the "did migrations run here" probe.</summary>
    private const string TenantOwnedTable = "Invoice";

    /// <summary>Ties the schemas of one test-class instance together for setup and teardown.</summary>
    private readonly string _runId = Guid.NewGuid().ToString("N");

    private readonly string _baseConnectionString =
        Environment.GetEnvironmentVariable(ConnectionStringEnvVar) ?? DefaultConnectionString;

    private NpgsqlDataSource? _adminDataSource;
    private NpgsqlDataSourceFactory? _targetFactory;
    private NpgsqlDataSourceFactory? _sourceFactory;
    private bool _databaseAvailable;

    /// <summary>Legacy single-database installation the tool reads from.</summary>
    private string SourceSchema => $"test_mig_src_{_runId}";

    /// <summary>Stands in for the target database's master ("public") schema.</summary>
    private string MasterSchema => $"test_mig_mst_{_runId}";

    /// <summary>
    /// Deliberately NOT canonical. The capital "T" is exactly what <see cref="SchemaNames.Sanitize"/>
    /// removes, so it is what makes the created and the persisted name diverge the moment the tool
    /// composes them twice. This prefix is the whole point of the fixture.
    /// </summary>
    private string NonCanonicalTenantPrefix => $"Tenant_{_runId}_";

    // ─── Fixture lifetime ─────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        // Pin the switch explicitly, even though OFF is Npgsql's own default: it is
        // process-global, Npgsql reads it once and then freezes it, so leaving it implicit
        // would make this fixture depend on whatever else the runner process did first.
        //
        // OFF deliberately deviates from Fakvio.MigrationTool/Program.cs, which turns it ON
        // for the sake of legacy non-UTC DateTimes. With it ON, legacy mode maps DateTime to
        // "timestamp without time zone" while every migration snapshot declares "timestamp
        // with time zone", so the model no longer matches the snapshot and
        // MasterDbContext.Database.MigrateAsync throws PendingModelChangesWarning at step 1 —
        // before the tool ever reaches a company. That is a pre-existing defect of the tool
        // (its own contexts do not suppress the warning, unlike ServiceCollectionExtensions
        // and TenantProvisioningService, which both do) and it is orthogonal to what this
        // class pins: schema naming is pure string composition and does not involve a single
        // DateTime. Turning the switch on here would only hide that invariant behind an
        // unrelated failure. Reported separately; revisit once the tool suppresses it too.
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);

        _adminDataSource = new NpgsqlDataSourceBuilder(_baseConnectionString).Build();
        _databaseAvailable = await CanReachPostgreSqlAsync();

        if (!_databaseAvailable)
            return; // Every test skips with SkipReason.

        // Deliberately NOT wrapped in try/catch: the reachability probe above is the only
        // reason this class may skip. Once the server answers, anything failing here is a
        // real defect and must fail loudly rather than turn into a silently-green skip.
        await ExecuteAsync($"CREATE SCHEMA \"{SourceSchema}\"");
        await ExecuteAsync($"CREATE SCHEMA \"{MasterSchema}\"");

        _targetFactory = NpgsqlDataSourceFactory.Create(BuildConfiguration());
        _sourceFactory = NpgsqlDataSourceFactory.Create(
            BuildConfiguration(), sectionName: "SourceDatabase", connectionStringName: "SourceConnection");

        await CreateLegacyDatabaseAsync();
    }

    /// <summary>
    /// Opens and immediately closes one raw connection. EF wraps connection failures in a
    /// generic "transient failure" <see cref="InvalidOperationException"/>, indistinguishable
    /// from a genuine bug — so the reachability question is answered at the driver level,
    /// where the exception types are unambiguous.
    /// </summary>
    private async Task<bool> CanReachPostgreSqlAsync()
    {
        try
        {
            await using var connection = await _adminDataSource!.OpenConnectionAsync();
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or SocketException)
        {
            return false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_targetFactory is not null)
            await _targetFactory.DisposeAsync();

        if (_sourceFactory is not null)
            await _sourceFactory.DisposeAsync();

        if (_databaseAvailable && _adminDataSource is not null)
        {
            // Drop by GUID rather than by name: the tenant schema's name depends on the company
            // id the migration happened to allocate, and a regressed build may have created a
            // differently-cased one. The GUID is unique to this instance, so this can never
            // reach anything the test did not create.
            foreach (var schema in await ListSchemasOfThisRunAsync())
                await ExecuteAsync($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
        }

        if (_adminDataSource is not null)
            await _adminDataSource.DisposeAsync();
    }

    // ─── Arrange helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// The configuration the tool reads from its appsettings.json, with both databases pinned
    /// to this instance's throwaway schemas via the connection string's Search Path. Neither
    /// SourceDbContext nor MasterDbContext sets a default schema, so search_path alone decides
    /// where their tables live — which is what makes the throwaway schemas possible at all.
    /// </summary>
    private IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = WithSearchPath(MasterSchema),
                ["ConnectionStrings:SourceConnection"] = WithSearchPath(SourceSchema),
                ["Database:AuthMode"] = nameof(DatabaseAuthMode.Password),
                ["SourceDatabase:AuthMode"] = nameof(DatabaseAuthMode.Password),
                ["Migration:DryRun"] = "false",
                ["Migration:SkipProvisionedCompanies"] = "false",
                ["Migration:TenantSchemaPrefix"] = NonCanonicalTenantPrefix
            })
            .Build();

    private string WithSearchPath(string schema) =>
        new NpgsqlConnectionStringBuilder(_baseConnectionString) { SearchPath = schema }.ConnectionString;

    /// <summary>
    /// Builds the legacy single-database schema the tool migrates away from and seeds it with
    /// exactly one issuer — enough to reach the per-company loop, and nothing more, so the test
    /// stays about schema naming rather than about copying business data.
    /// </summary>
    private async Task CreateLegacyDatabaseAsync()
    {
        await using var source = CreateSourceContext();

        // CreateTablesAsync, not EnsureCreatedAsync: the "fakvio" database already exists and
        // EnsureCreated is a no-op the moment the DATABASE is there — it never looks at schemas.
        await source.Database.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();

        source.Client.Add(new Client
        {
            CompanyName = SourceCompanyName,
            RegistrationNumber = SourceRegistrationNumber,
            IsIssuer = true,
            IsActive = true
        });
        await source.SaveChangesAsync();
    }

    private SourceDbContext CreateSourceContext()
    {
        var options = new DbContextOptionsBuilder<SourceDbContext>()
            .UseNpgsql(_sourceFactory!.Root)
            .Options;

        return new SourceDbContext(options);
    }

    /// <summary>Reads back what the migration persisted — from the database, not from a change tracker.</summary>
    private MasterDbContext CreateMasterContext()
    {
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseNpgsql(_targetFactory!.Root)
            .Options;

        return new MasterDbContext(options);
    }

    private DataMigrationService CreateMigrationService() =>
        new(BuildConfiguration(),
            NullLogger<DataMigrationService>.Instance,
            _targetFactory!,
            _sourceFactory!);

    // ─── Database probes ──────────────────────────────────────────────────────

    private async Task ExecuteAsync(string sql)
    {
        await using var command = _adminDataSource!.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<bool> SchemaExistsAsync(string schemaName)
    {
        await using var command = _adminDataSource!.CreateCommand(
            "SELECT 1 FROM information_schema.schemata WHERE schema_name = @name");
        command.Parameters.AddWithValue("name", schemaName);
        return await command.ExecuteScalarAsync() is not null;
    }

    private async Task<List<string>> ListSchemasOfThisRunAsync()
    {
        await using var command = _adminDataSource!.CreateCommand(
            "SELECT schema_name FROM information_schema.schemata WHERE schema_name LIKE @pattern");
        command.Parameters.AddWithValue("pattern", $"%{_runId}%");

        var schemas = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            schemas.Add(reader.GetString(0));

        return schemas;
    }

    private async Task<bool> TableExistsAsync(string schemaName, string tableName)
    {
        await using var command = _adminDataSource!.CreateCommand(
            "SELECT 1 FROM information_schema.tables WHERE table_schema = @schema AND table_name = @table");
        command.Parameters.AddWithValue("schema", schemaName);
        command.Parameters.AddWithValue("table", tableName);
        return await command.ExecuteScalarAsync() is not null;
    }

    /// <summary>
    /// Runs the whole tool once and hands back the settings row it wrote. Failing here on an
    /// unsuccessful run keeps every test's assertions about that row rather than about plumbing.
    /// </summary>
    private async Task<CompanySystemSettings> RunMigrationAndReadSettingsAsync()
    {
        var succeeded = await CreateMigrationService().MigrateAsync();
        succeeded.ShouldBeTrue("The migration reported errors — see the tool's own logging.");

        await using var master = CreateMasterContext();
        return await master.CompanySystemSettings.AsNoTracking().SingleAsync();
    }

    // ─── Tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The invariant in its purest form: whatever name ends up in CompanySystemSettings must
    /// name a schema that actually exists. That is precisely what runtime tenant resolution
    /// relies on, and precisely what a second, un-canonicalized composition breaks.
    /// </summary>
    [SkippableFact]
    public async Task Migration_WithNonCanonicalSchemaPrefix_PersistsTheSchemaNameItCreated()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var settings = await RunMigrationAndReadSettingsAsync();

        (await SchemaExistsAsync(settings.SchemaName)).ShouldBeTrue(
            $"CompanySystemSettings.SchemaName is '{settings.SchemaName}', but no such schema exists. " +
            "The migration created its schema through SchemaNames.Sanitize and persisted a " +
            "differently composed name, so this tenant is unreachable at runtime.");
    }

    /// <summary>
    /// The same defect stated as a property of the stored value alone: it must already be
    /// canonical, so sanitizing it again changes nothing. This is the form a future reader can
    /// check without a schema lookup, and it also fails on a merely partial normalization.
    /// </summary>
    [SkippableFact]
    public async Task Migration_WithNonCanonicalSchemaPrefix_PersistsACanonicalSchemaName()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var settings = await RunMigrationAndReadSettingsAsync();

        settings.SchemaName.ShouldBe(SchemaNames.Sanitize(settings.SchemaName));
        settings.SchemaName.ShouldBe(SchemaNames.Sanitize($"{NonCanonicalTenantPrefix}{settings.CompanyId}"));
    }

    /// <summary>
    /// Guards the other half of the change (issue #136: <c>includePublicInSearchPath: false</c>).
    /// The tenant DbContext is built without an explicit schema and resolves purely through
    /// search_path, so the tenant migrations must land inside the tenant schema. Were "public"
    /// — here the master schema — in that path, a table still missing from the half-built tenant
    /// schema would silently resolve to the master one and tenant data would be written to master.
    /// </summary>
    [SkippableFact]
    public async Task Migration_RunsTenantMigrationsInsideTheTenantSchemaOnly()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var settings = await RunMigrationAndReadSettingsAsync();

        (await TableExistsAsync(settings.SchemaName, TenantOwnedTable)).ShouldBeTrue(
            $"The tenant schema has no {TenantOwnedTable} table — the tenant migrations did not run inside it.");
        (await TableExistsAsync(MasterSchema, TenantOwnedTable)).ShouldBeFalse(
            $"An {TenantOwnedTable} table appeared in the master schema — tenant migrations leaked through search_path.");
    }
}
