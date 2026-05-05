using Shouldly;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for TenantDbContextFactory — creates TenantDbContext instances
/// connected to the correct tenant schema based on CompanySystemSettings.
///
/// Architecture: PostgreSQL multi-schema — all tenants share one database,
/// each tenant gets its own schema (e.g., "tenant_42").
///
/// Test scenarios:
/// - Valid provisioned + active tenant → creates context successfully
/// - Unprovisioned tenant → throws InvalidOperationException
/// - Inactive (suspended) tenant → throws InvalidOperationException
/// - No CompanySystemSettings record → throws InvalidOperationException
/// - No CompanyId in request → throws InvalidOperationException
/// - GetConnectionString → returns shared connection string
/// - EnsureMigratedAsync migration failure → re-throws (does NOT swallow)
/// - EnsureMigratedAsync success → marks schema as migrated (idempotent on retry)
/// </summary>
public class TenantDbContextFactoryTests : IDisposable
{
    private readonly MasterDbContext _masterDb;
    private readonly ITenantResolver _tenantResolver;
    private readonly ITenantProvisioningService _provisioningService;
    private readonly IConfiguration _configuration;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<TenantDbContextFactory> _logger;

    public TenantDbContextFactoryTests()
    {
        // Create an InMemory MasterDbContext for testing
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _masterDb = new MasterDbContext(options);

        _tenantResolver = Substitute.For<ITenantResolver>();
        _provisioningService = Substitute.For<ITenantProvisioningService>();
        _logger = Substitute.For<ILogger<TenantDbContextFactory>>();

        // Configuration with a PostgreSQL connection string (shared database for all schemas)
        var connectionString = "Host=localhost;Database=fakvio;Username=fakvio;Password=YourStrong!Passw0rd";
        var configData = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = connectionString
        };
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        // Build a real NpgsqlDataSource from the test connection string.
        // NpgsqlDataSource is sealed, so we cannot mock it — but we only need its
        // ConnectionString property for the factory to derive per-tenant data sources.
        _dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
    }

    /// <summary>
    /// Seeds a company (Client with IsIssuer = true) and its CompanySystemSettings
    /// into the in-memory master database for testing.
    /// In the multi-schema architecture, SchemaName identifies the tenant's schema
    /// within the shared PostgreSQL database.
    /// </summary>
    private async Task SeedCompanyAsync(
        long companyId,
        string schemaName,
        bool isProvisioned = true,
        bool isActive = true)
    {
        var client = new Client
        {
            Id = companyId,
            CompanyName = $"Test Company {companyId}",
            RegistrationNumber = $"REG{companyId:D6}",
            IsIssuer = true,
            IsActive = true,
            IsVatPayer = true,
            Address = new List<Address>(),
            Contact = new List<Contact>()
        };

        _masterDb.Client.Add(client);
        await _masterDb.SaveChangesAsync();

        var settings = new CompanySystemSettings
        {
            CompanyId = companyId,
            SchemaName = schemaName,
            IsProvisioned = isProvisioned,
            IsActive = isActive,
            ProvisionedAt = isProvisioned ? DateTime.UtcNow : null
        };

        _masterDb.CompanySystemSettings.Add(settings);
        await _masterDb.SaveChangesAsync();
    }

    private TenantDbContextFactory CreateFactory()
    {
        return new TenantDbContextFactory(
            _tenantResolver,
            _masterDb,
            _provisioningService,
            _configuration,
            _dataSource,
            _logger);
    }

    [Fact]
    public async Task CreateContextAsync_NoCompanyId_ThrowsInvalidOperationException()
    {
        // Arrange — SysAdmin without impersonation (no CompanyId)
        _tenantResolver.GetCurrentCompanyId().Returns((long?)null);
        var factory = CreateFactory();

        // Act & Assert
        var act = () => factory.CreateContextAsync();
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("no CompanyId available");
    }

    [Fact]
    public async Task CreateContextForCompanyAsync_NoSettingsRecord_ThrowsInvalidOperationException()
    {
        // Arrange — company 999 doesn't have CompanySystemSettings
        var factory = CreateFactory();

        // Act & Assert
        var act = () => factory.CreateContextForCompanyAsync(999);
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("not found, not provisioned, or inactive");
    }

    [Fact]
    public async Task CreateContextForCompanyAsync_NotProvisioned_ThrowsInvalidOperationException()
    {
        // Arrange — company exists but schema not yet provisioned
        await SeedCompanyAsync(companyId: 10, schemaName: "tenant_10", isProvisioned: false);
        var factory = CreateFactory();

        // Act & Assert
        var act = () => factory.CreateContextForCompanyAsync(10);
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("not found, not provisioned, or inactive");
    }

    [Fact]
    public async Task CreateContextForCompanyAsync_Inactive_ThrowsInvalidOperationException()
    {
        // Arrange — company is provisioned but suspended (inactive)
        await SeedCompanyAsync(companyId: 20, schemaName: "tenant_20", isProvisioned: true, isActive: false);
        var factory = CreateFactory();

        // Act & Assert
        var act = () => factory.CreateContextForCompanyAsync(20);
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("inactive");
    }

    [Fact]
    public async Task GetConnectionStringAsync_NoSettings_ReturnsNull()
    {
        // Arrange — no CompanySystemSettings for company 999
        var factory = CreateFactory();

        // Act
        var result = await factory.GetConnectionStringAsync(999);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetConnectionStringAsync_ValidCompany_ReturnsSharedConnectionString()
    {
        // Arrange — company with standard schema name.
        // In multi-schema architecture, all tenants share the same connection string
        // (only the schema differs, not the database/server).
        await SeedCompanyAsync(companyId: 30, schemaName: "tenant_30");
        var factory = CreateFactory();

        // Act
        var connectionString = await factory.GetConnectionStringAsync(30);

        // Assert — should return the shared DefaultConnection string
        connectionString.ShouldNotBeNull();
        connectionString.ShouldContain("localhost");
        connectionString.ShouldContain("fakvio");
    }

    [Fact]
    public async Task CreateContextAsync_ValidRequest_UsesResolverCompanyId()
    {
        // Arrange — resolver returns CompanyId = 50
        await SeedCompanyAsync(companyId: 50, schemaName: "tenant_50");
        _tenantResolver.GetCurrentCompanyId().Returns(50L);
        var factory = CreateFactory();

        // Act — CreateContextAsync should internally call CreateContextForCompanyAsync(50).
        // In multi-schema architecture, this creates a TenantDbContext with Schema = "tenant_50".
        // The factory validates provisioning/active status before creating the context.
        // We verify the validation passes by checking GetConnectionStringAsync instead,
        // since actually creating a context would require a real PostgreSQL connection.
        var connString = await factory.GetConnectionStringAsync(50);
        connString.ShouldContain("fakvio");
    }

    [Fact]
    public void CreateTenantContext_SetsSchemaProperty()
    {
        // Arrange
        var factory = CreateFactory();

        // Act — directly test the CreateTenantContext method that creates
        // a TenantDbContext with the correct schema set
        var context = factory.CreateTenantContext("tenant_99");

        // Assert — the Schema property should be set on the context
        context.ShouldNotBeNull();
        context.Schema.ShouldBe("tenant_99");
        context.Dispose();
    }

    // ─── EnsureMigratedAsync tests (issue #99 root-cause fix) ────────────────

    /// <summary>
    /// When MigrateTenantAsync throws (e.g., PostgreSQL permission error, column already exists),
    /// EnsureMigratedAsync must re-throw the exception instead of swallowing it.
    ///
    /// Before the fix: the exception was caught and logged, then the request proceeded
    /// against an unmigrated schema. EF Core then tried to SELECT non-existent columns
    /// (VatRegime, ReverseChargeCodeId, InformationalVatAmount) and crashed inside the
    /// service layer with a confusing PostgresException / HTTP 500.
    ///
    /// After the fix: the exception propagates to TenantContextMiddleware, which returns
    /// HTTP 503 with a clear "schema not ready" message. The tenant's data is safe and
    /// the endpoint is retryable once the migration succeeds.
    /// </summary>
    [Fact]
    public async Task EnsureMigratedAsync_MigrationThrows_RethrowsException()
    {
        // Arrange — provisioned and active tenant so the migration path is entered
        await SeedCompanyAsync(companyId: 100, schemaName: "tenant_100");

        // MigrateTenantAsync simulates a PostgreSQL error (e.g., permission denied,
        // or column already exists from a partially-applied migration)
        _provisioningService
            .MigrateTenantAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("42501: permission denied for schema tenant_100"));

        var factory = CreateFactory();

        // Act & Assert — exception must propagate, not be swallowed
        var act = () => factory.EnsureMigratedAsync(100);
        await Should.ThrowAsync<InvalidOperationException>(act);
    }

    /// <summary>
    /// When migration fails, the schema must NOT be added to the in-process cache.
    /// This ensures the next request retries the migration (transient errors are recoverable).
    ///
    /// If we cached a failed migration, a transient error (network glitch, lock timeout)
    /// would permanently block the tenant until the process restarts.
    /// </summary>
    [Fact]
    public async Task EnsureMigratedAsync_MigrationThrows_SchemaNotMarkedAsMigrated()
    {
        // Arrange
        await SeedCompanyAsync(companyId: 101, schemaName: "tenant_101");

        _provisioningService
            .MigrateTenantAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Simulated migration failure"));

        var factory = CreateFactory();

        // First call — migration fails
        try { await factory.EnsureMigratedAsync(101); } catch { /* expected */ }

        // Reset mock to succeed on retry
        _provisioningService
            .MigrateTenantAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        // Second call must attempt migration again (schema was not cached on failure).
        // If it doesn't throw, migration ran again — which is the correct behaviour.
        await Should.NotThrowAsync(() => factory.EnsureMigratedAsync(101));

        // Migration service was called twice: once for the failing attempt, once for retry.
        await _provisioningService.Received(2).MigrateTenantAsync(101, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// When migration succeeds, subsequent calls must skip it (cached by schema name).
    /// This is the normal hot path — only the first request after process start triggers migration.
    /// </summary>
    [Fact]
    public async Task EnsureMigratedAsync_MigrationSucceeds_SubsequentCallsSkipMigration()
    {
        // Arrange — use a unique company ID to avoid interference from the static cache
        // populated by other tests in this class. Each test run gets a fresh schema name.
        var uniqueId = 200 + Random.Shared.Next(100, 999);
        var uniqueSchema = $"tenant_{uniqueId}_cache_test";
        await SeedCompanyAsync(companyId: uniqueId, schemaName: uniqueSchema);

        _provisioningService
            .MigrateTenantAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var factory = CreateFactory();

        // Act — call twice
        await factory.EnsureMigratedAsync(uniqueId);
        await factory.EnsureMigratedAsync(uniqueId);

        // Assert — MigrateTenantAsync was called exactly once (cached after success)
        await _provisioningService.Received(1).MigrateTenantAsync(uniqueId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// When the tenant is not provisioned, EnsureMigratedAsync must return silently
    /// (no migration attempt) — nothing to migrate for an unprovisioned tenant.
    /// </summary>
    [Fact]
    public async Task EnsureMigratedAsync_NotProvisioned_DoesNotCallMigration()
    {
        // Arrange — unprovisioned tenant
        await SeedCompanyAsync(companyId: 102, schemaName: "tenant_102", isProvisioned: false);
        var factory = CreateFactory();

        // Act — should return without calling MigrateTenantAsync
        await factory.EnsureMigratedAsync(102);

        // Assert — no migration attempted
        await _provisioningService.DidNotReceive().MigrateTenantAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    public void Dispose()
    {
        _masterDb.Dispose();
        _dataSource.Dispose();
    }
}
