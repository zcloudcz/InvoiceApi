using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Marks a test that talks to a real PostgreSQL server. The test only runs when the
/// <c>FAKVIO_DB_SMOKE=1</c> environment variable is set; otherwise it is reported as
/// <i>skipped</i>, with a reason explaining how to enable it.
///
/// Why an attribute and not a runtime check: xUnit 2.x has no <c>Assert.Skip</c> (that arrived
/// in xUnit v3), and throwing a skip exception at runtime is reported as a failure by the
/// current runner. Overriding <see cref="FactAttribute.Skip"/> is the supported v2 way — the
/// value is read during test discovery, so the test class is never even constructed.
///
/// Why not <c>[SkippableFact]</c> (Xunit.SkippableFact, already used by
/// <c>Fakvio.Tests.Integration/EpoSandboxSmokeTests.cs</c>): its <c>Skip.If(...)</c> call sits
/// inside the test body, so xUnit constructs the test class first - and this class's
/// constructor does real work (reads appsettings.json with <c>optional: false</c>, runs
/// <c>DatabaseOptions.Resolve</c> + <c>Validate()</c>, builds a real
/// <see cref="Npgsql.NpgsqlDataSource"/>). A constructor exception is reported as a failure,
/// not as a skip, which would let the chronic red state this gate exists to remove back in
/// through the side door. Measured on these exact versions (xunit 2.9.3, SkippableFact
/// 1.4.13): a throwing constructor behind [SkippableFact] is reported [FAIL]; the same
/// constructor behind an attribute gate is [SKIP] and never runs.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DatabaseSmokeFactAttribute : FactAttribute
{
    /// <summary>
    /// Opt-in switch. Only the exact value "1" enables the tests: an unambiguous opt-in is what
    /// guarantees a plain <c>dotnet test</c> can never go red because of a missing database.
    /// </summary>
    public const string EnableEnvironmentVariable = "FAKVIO_DB_SMOKE";

    /// <summary>
    /// Shown on every skipped test, so it has to be actionable: which variable to set, what has
    /// to be running, and how to point the run at local Docker instead of the committed Azure
    /// configuration.
    /// </summary>
    public const string DisabledReason =
        "Database connectivity smoke test is disabled. Set " + EnableEnvironmentVariable + "=1 to run it " +
        "against a reachable, migrated PostgreSQL server. " +
        "Azure PostgreSQL (the committed default configuration): run 'az login' first, no overrides needed. " +
        "Local Docker ('docker compose up -d'): override the committed configuration with the environment " +
        "variables ConnectionStrings__DefaultConnection=Host=localhost;Port=5432;Database=fakvio;" +
        "Username=fakvio;Password=fakvio_dev and Database__AuthMode=Password. " +
        "The legacy 'UseAzureAdAuthentication' key is no longer in Fakvio.API/appsettings.json, but if your " +
        "environment still sets it, set it to false too — DatabaseOptions.Resolve rejects a pair that disagrees.";

    /// <summary>
    /// True when the smoke tests were explicitly enabled for this process.
    /// </summary>
    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable(EnableEnvironmentVariable) == "1";

    /// <inheritdoc />
    public override string? Skip
    {
        get => IsEnabled ? base.Skip : DisabledReason;
        set => base.Skip = value;
    }
}

/// <summary>
/// Connectivity smoke test: verifies that the configuration the application really resolves at
/// startup can actually open a PostgreSQL connection, and that the database behind it is
/// migrated and queryable.
///
/// Unlike every other test in this project, these tests talk to a real database, so they are
/// gated behind <see cref="DatabaseSmokeFactAttribute"/> (see <c>FAKVIO_DB_SMOKE</c>). A plain
/// <c>dotnet test</c> on a machine with no PostgreSQL and no <c>az login</c> reports them as
/// skipped, never as failures.
///
/// Configuration is resolved through
/// <see cref="NpgsqlDataSourceFactory.Create(IConfiguration, string, string)"/> — the same entry
/// point the production composition root uses — so the test exercises the real auth-mode
/// resolution (Password vs. Entra ID) rather than a hand-rolled connection string.
/// </summary>
public sealed class DatabaseConnectivitySmokeTests : IDisposable
{
    private readonly NpgsqlDataSourceFactory _dataSourceFactory;
    private readonly MasterDbContext _masterContext;
    private readonly TenantDbContext _tenantContext;

    /// <summary>
    /// Runs once per test. xUnit constructs a test class only for tests it is actually going to
    /// execute, so while the smoke gate is closed nothing here — no file read, no data source —
    /// ever happens.
    /// </summary>
    public DatabaseConnectivitySmokeTests()
    {
        // Must run before anything touches Npgsql's type mapping. The three production entry
        // points (Fakvio.API, Fakvio.Functions, Fakvio.MigrationTool) set the same switch in
        // their Program.cs, but the test host does not — without it the test would read
        // timestamps with different semantics than the application does.
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        // Create() = DatabaseOptions.Resolve + Validate + factory construction. That is exactly
        // what the application does at startup, which is the whole point of a smoke test:
        // a wrong auth mode or connection string fails here the same way production would fail.
        _dataSourceFactory = NpgsqlDataSourceFactory.Create(BuildConfiguration());

        _masterContext = new MasterDbContext(BuildContextOptions<MasterDbContext>(_dataSourceFactory));
        _tenantContext = new TenantDbContext(BuildContextOptions<TenantDbContext>(_dataSourceFactory));
    }

    public void Dispose()
    {
        _masterContext.Dispose();
        _tenantContext.Dispose();

        // The factory owns the data source both contexts were built on, so it goes last.
        _dataSourceFactory.Dispose();
    }

    [DatabaseSmokeFact]
    public async Task MasterDbContext_ShouldConnect_ToPostgreSQL()
    {
        // Act — CanConnectAsync() opens a connection and sends a simple query
        var canConnect = await _masterContext.Database.CanConnectAsync();

        // Assert
        canConnect.ShouldBeTrue(
            "Cannot connect to PostgreSQL with the resolved configuration. Is the server reachable? " +
            "(local Docker: docker compose up -d; Azure: az login)");
    }

    [DatabaseSmokeFact]
    public async Task TenantDbContext_ShouldConnect_ToPostgreSQL()
    {
        // Act
        var canConnect = await _tenantContext.Database.CanConnectAsync();

        // Assert
        canConnect.ShouldBeTrue(
            "Cannot connect to PostgreSQL with the resolved configuration. Is the server reachable? " +
            "(local Docker: docker compose up -d; Azure: az login)");
    }

    [DatabaseSmokeFact]
    public async Task MasterDbContext_ShouldHave_MigrationsApplied()
    {
        // Act — get the list of applied migrations from the __EFMigrationsHistory table
        var appliedMigrations = await _masterContext.Database.GetAppliedMigrationsAsync();

        // Assert — at least the InitPostgres migration should be applied
        appliedMigrations.ShouldNotBeEmpty(
            "No migrations applied to master database. Run: dotnet ef database update --context MasterDbContext");
    }

    [DatabaseSmokeFact]
    public async Task MasterDbContext_ShouldQuery_UsersTable()
    {
        // Act — simple query against the Users table in the public schema.
        // This verifies the schema and table structure is correct.
        var userCount = await _masterContext.User.CountAsync();

        // Assert — count can be 0 (empty DB) or more, but the query must succeed
        userCount.ShouldBeGreaterThanOrEqualTo(0,
            "Failed to query Users table. Schema might be incorrect.");
    }

    /// <summary>
    /// Loads the API's own configuration files (so the smoke test measures what the application
    /// is actually configured to do), then layers environment variables on top so a run can be
    /// pointed at local Docker without editing committed files.
    /// </summary>
    private static IConfigurationRoot BuildConfiguration() =>
        new ConfigurationBuilder()
            .SetBasePath(Path.Combine(
                Directory.GetCurrentDirectory(), "..", "..", "..", "..", "Fakvio.API"))
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

    /// <summary>
    /// Builds EF Core options on the factory's shared data source. There is deliberately no
    /// <c>UseNpgsql(string)</c> overload here: going through the data source is what puts the
    /// Entra ID token provider (when that auth mode is active) into the tested path.
    /// </summary>
    private static DbContextOptions<TContext> BuildContextOptions<TContext>(INpgsqlDataSourceFactory factory)
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(factory.Root, b => b.MigrationsAssembly("Fakvio.Infrastructure"))
            .Options;
}
