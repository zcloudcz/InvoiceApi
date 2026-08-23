using System.Net.Sockets;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
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
/// Shared plumbing for every test that drives the real <see cref="Fakvio.MigrationTool"/> against a
/// REAL PostgreSQL server. Everything the tool does — creating schemas, running EF migrations inside
/// them, resolving tables through <c>search_path</c> — is database behaviour, so an in-memory or
/// mocked provider would prove nothing about it.
///
/// Isolation: xUnit builds a fresh instance of a test class for every test method, so each test owns
/// a GUID and three throwaway schemas derived from it (source, master, tenant). <see cref="DisposeAsync"/>
/// drops every schema whose name contains that GUID, so a run never touches developer data and two
/// runs never collide. The tenant prefix is GUID-scoped as well — a bare <c>"Tenant_"</c> would
/// compose <c>tenant_1</c> and collide with a real tenant schema.
///
/// Skipped (not failed) when PostgreSQL is unreachable — start it with <c>docker compose up -d</c>,
/// or point <c>FAKVIO_TEST_POSTGRES</c> elsewhere. See DEVGUIDE §8.2.1 for the shared
/// throwaway-schema pattern.
/// </summary>
public abstract class LiveMigrationToolTest : IAsyncLifetime
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

    /// <summary>
    /// The one legacy user. Its e-mail is NOT arbitrary: the master init migration seeds this very
    /// account (see Migrations/Master/…_InitPostgres.cs), a legacy single-database installation has
    /// it for the same reason, and MigrateUsersAsync matches users by e-mail. Any other address
    /// would leave the target with one user more than the source — which DataIntegrityVerifier
    /// reports as a failed check, even though nothing went wrong.
    /// </summary>
    private const string SeededAdminEmail = "admin@zcloud.cz";

    /// <summary>Ties the schemas of one test-class instance together for setup and teardown.</summary>
    private readonly string _runId = Guid.NewGuid().ToString("N");

    private readonly string _baseConnectionString =
        Environment.GetEnvironmentVariable(ConnectionStringEnvVar) ?? DefaultConnectionString;

    private NpgsqlDataSource? _adminDataSource;
    private NpgsqlDataSourceFactory? _targetFactory;
    private NpgsqlDataSourceFactory? _sourceFactory;
    private bool _databaseAvailable;

    /// <summary>Legacy single-database installation the tool reads from.</summary>
    protected string SourceSchema => $"test_mig_src_{_runId}";

    /// <summary>Stands in for the target database's master ("public") schema.</summary>
    protected string MasterSchema => $"test_mig_mst_{_runId}";

    /// <summary>
    /// Deliberately NOT canonical. The capital "T" is exactly what <see cref="SchemaNames.Sanitize"/>
    /// removes, so it is what makes the created and the persisted name diverge the moment the tool
    /// composes them twice. This prefix is the whole point of the fixture.
    /// </summary>
    protected string NonCanonicalTenantPrefix => $"Tenant_{_runId}_";

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
        // and TenantProvisioningService, which both do) and it is orthogonal to what these
        // tests pin. Turning the switch on here would only hide the invariants behind an
        // unrelated failure. Reported separately; revisit once the tool suppresses it too.
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);

        _adminDataSource = new NpgsqlDataSourceBuilder(_baseConnectionString).Build();
        _databaseAvailable = await CanReachPostgreSqlAsync();

        if (!_databaseAvailable)
            return; // Every test skips with SkipReason.

        // Deliberately NOT wrapped in try/catch: the reachability probe above is the only
        // reason these classes may skip. Once the server answers, anything failing here is a
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

    /// <summary>Turns "no database here" into a skip; every test starts with this line.</summary>
    protected void SkipIfDatabaseUnavailable() => Skip.IfNot(_databaseAvailable, SkipReason);

    // ─── Arrange helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// The configuration the tool reads from its appsettings.json, with both databases pinned
    /// to this instance's throwaway schemas via the connection string's Search Path. Neither
    /// SourceDbContext nor MasterDbContext sets a default schema, so search_path alone decides
    /// where their tables live — which is what makes the throwaway schemas possible at all.
    /// </summary>
    private IConfiguration BuildConfiguration(bool dryRun = false) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = WithSearchPath(MasterSchema),
                ["ConnectionStrings:SourceConnection"] = WithSearchPath(SourceSchema),
                ["Database:AuthMode"] = nameof(DatabaseAuthMode.Password),
                ["SourceDatabase:AuthMode"] = nameof(DatabaseAuthMode.Password),
                ["Migration:DryRun"] = dryRun ? "true" : "false",
                ["Migration:SkipProvisionedCompanies"] = "false",
                ["Migration:TenantSchemaPrefix"] = NonCanonicalTenantPrefix
            })
            .Build();

    private string WithSearchPath(string schema) =>
        new NpgsqlConnectionStringBuilder(_baseConnectionString) { SearchPath = schema }.ConnectionString;

    /// <summary>
    /// Builds the legacy single-database schema the tool migrates away from and seeds it with
    /// exactly one issuer — enough to reach the per-company loop, and nothing more, so the tests
    /// stay about the tool's plumbing rather than about copying business data.
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

        source.User.Add(new User
        {
            Email = SeededAdminEmail,
            FirstName = "System",
            LastName = "Administrator",
            Role = EUserRole.SysAdmin,
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
    protected MasterDbContext CreateMasterContext()
    {
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseNpgsql(_targetFactory!.Root)
            .Options;

        return new MasterDbContext(options);
    }

    protected DataMigrationService CreateMigrationService(bool dryRun = false) =>
        new(BuildConfiguration(dryRun),
            NullLogger<DataMigrationService>.Instance,
            _targetFactory!,
            _sourceFactory!);

    /// <summary>
    /// The verifier reports WHICH of its checks failed only through its logger, so the log is
    /// captured and handed back with the instance — a bare "expected True, was False" from CI
    /// would otherwise be unactionable.
    /// </summary>
    protected (DataIntegrityVerifier Verifier, RecordedLog Log) CreateVerifier()
    {
        var log = new RecordedLog();
        return (new DataIntegrityVerifier(_targetFactory!, _sourceFactory!, log), log);
    }

    /// <summary>
    /// Brings the master schema up to date without running the migration. A dry run skips this
    /// step itself, because in real life it is pointed at a target database that already exists.
    /// </summary>
    protected async Task ApplyMasterMigrationsAsync()
    {
        await using var master = CreateMasterContext();
        await master.Database.MigrateAsync();
    }

    // ─── Database probes ──────────────────────────────────────────────────────

    private async Task ExecuteAsync(string sql)
    {
        await using var command = _adminDataSource!.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    protected async Task<bool> SchemaExistsAsync(string schemaName)
    {
        await using var command = _adminDataSource!.CreateCommand(
            "SELECT 1 FROM information_schema.schemata WHERE schema_name = @name");
        command.Parameters.AddWithValue("name", schemaName);
        return await command.ExecuteScalarAsync() is not null;
    }

    protected async Task<List<string>> ListSchemasOfThisRunAsync()
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

    protected async Task<bool> TableExistsAsync(string schemaName, string tableName)
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
    protected async Task<CompanySystemSettings> RunMigrationAndReadSettingsAsync()
    {
        var succeeded = await CreateMigrationService().MigrateAsync();
        succeeded.ShouldBeTrue("The migration reported errors — see the tool's own logging.");

        await using var master = CreateMasterContext();
        return await master.CompanySystemSettings.AsNoTracking().SingleAsync();
    }
}
