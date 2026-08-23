using System.Data.Common;
using System.Reflection;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for TenantProvisioningService.
///
/// Tests the service's logic for managing tenant lifecycle:
/// - Activate/deactivate tenants (flips IsActive flag in CompanySystemSettings)
/// - Provisioning validation (throws for missing settings, already provisioned)
/// - MigrateAllTenantsAsync (queries active tenants)
///
/// Architecture: PostgreSQL multi-schema — all tenants share one database,
/// each tenant gets its own schema (e.g., "tenant_42").
///
/// Note: Full provisioning (CREATE SCHEMA, apply migrations, copy code tables)
/// cannot be tested with InMemoryDatabase — those are integration test scenarios.
/// We focus on the service's state management and validation logic here.
/// </summary>
public class TenantProvisioningServiceTests : IDisposable
{
    private const string TestConnectionString =
        "Host=localhost;Database=fakvio;Username=fakvio;Password=test";

    /// <summary>
    /// Marker baked into the stubbed per-schema connection string so a test can tell the
    /// factory's data source apart from the root one.
    /// </summary>
    private const string SchemaDataSourceMarker = "fakvio-schema-source";

    private readonly MasterDbContext _masterContext;
    private readonly INpgsqlDataSourceFactory _dataSourceFactory;
    private readonly NpgsqlDataSource _rootDataSource;
    private readonly NpgsqlDataSource _schemaDataSource;
    private readonly ILogger<TenantProvisioningService> _logger;
    private readonly TenantProvisioningService _service;

    public TenantProvisioningServiceTests()
    {
        // In-memory MasterDbContext for tenant metadata management
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _masterContext = new MasterDbContext(options);

        _logger = Substitute.For<ILogger<TenantProvisioningService>>();

        // NpgsqlDataSource for unit tests — points to localhost, won't actually connect.
        // Building a data source only parses the connection string; no network I/O happens
        // until a connection is requested, so tests that trigger real DB operations fail at
        // SQL level (expected in unit tests).
        _rootDataSource = new NpgsqlDataSourceBuilder(TestConnectionString).Build();

        // Stand-in for what the real factory hands out for a tenant schema: same host, but
        // tagged via ApplicationName so a test can prove the context uses THIS instance.
        _schemaDataSource = new NpgsqlDataSourceBuilder(
            $"{TestConnectionString};Application Name={SchemaDataSourceMarker}").Build();

        // The factory is substituted: these tests assert WHICH data source the service asks
        // for, not how the factory builds one (that is covered by NpgsqlDataSourceFactoryTests).
        _dataSourceFactory = Substitute.For<INpgsqlDataSourceFactory>();
        _dataSourceFactory.Root.Returns(_rootDataSource);
        _dataSourceFactory.GetForSchema(Arg.Any<string>(), Arg.Any<bool>()).Returns(_schemaDataSource);

        _service = new TenantProvisioningService(_masterContext, _dataSourceFactory, _rootDataSource, _logger);
    }

    /// <summary>
    /// Seeds a company (Client with IsIssuer = true) in the master database.
    /// Required for FK relationship with CompanySystemSettings.
    /// </summary>
    private async Task<Client> SeedCompanyAsync(long id = 1)
    {
        var client = new Client
        {
            Id = id,
            CompanyName = $"Test Company {id}",
            RegistrationNumber = $"REG{id:D8}",
            IsIssuer = true,
            IsActive = true,
            IsVatPayer = true,
            Address = new List<Address>(),
            Contact = new List<Contact>()
        };

        _masterContext.Client.Add(client);
        await _masterContext.SaveChangesAsync();
        return client;
    }

    /// <summary>
    /// Seeds CompanySystemSettings for a company.
    /// The company must be seeded first via SeedCompanyAsync.
    /// </summary>
    private async Task<CompanySystemSettings> SeedSettingsAsync(
        long companyId,
        bool isProvisioned = false,
        bool isActive = false)
    {
        var settings = new CompanySystemSettings
        {
            CompanyId = companyId,
            SchemaName = $"tenant_{companyId}",
            IsProvisioned = isProvisioned,
            IsActive = isActive,
            ProvisionedAt = isProvisioned ? DateTime.UtcNow : null
        };

        _masterContext.CompanySystemSettings.Add(settings);
        await _masterContext.SaveChangesAsync();
        return settings;
    }

    #region ActivateTenantAsync Tests

    [Fact]
    public async Task ActivateTenantAsync_ProvisionedTenant_SetsIsActiveTrue()
    {
        // Arrange — company with provisioned but inactive tenant
        await SeedCompanyAsync(1);
        await SeedSettingsAsync(1, isProvisioned: true, isActive: false);

        // Act
        var result = await _service.ActivateTenantAsync(1);

        // Assert
        result.ShouldBeTrue();
        var settings = await _masterContext.CompanySystemSettings
            .FirstOrDefaultAsync(s => s.CompanyId == 1);
        settings!.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task ActivateTenantAsync_NotFound_ReturnsFalse()
    {
        // Arrange — no settings exist for company 999

        // Act
        var result = await _service.ActivateTenantAsync(999);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task ActivateTenantAsync_NotProvisioned_ThrowsInvalidOperationException()
    {
        // Arrange — company with settings but NOT provisioned
        await SeedCompanyAsync(2);
        await SeedSettingsAsync(2, isProvisioned: false, isActive: false);

        // Act & Assert — can't activate what hasn't been provisioned
        var act = () => _service.ActivateTenantAsync(2);
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("not provisioned");
    }

    #endregion

    #region DeactivateTenantAsync Tests

    [Fact]
    public async Task DeactivateTenantAsync_ActiveTenant_SetsIsActiveFalse()
    {
        // Arrange — company with active tenant
        await SeedCompanyAsync(3);
        await SeedSettingsAsync(3, isProvisioned: true, isActive: true);

        // Act
        var result = await _service.DeactivateTenantAsync(3);

        // Assert
        result.ShouldBeTrue();
        var settings = await _masterContext.CompanySystemSettings
            .FirstOrDefaultAsync(s => s.CompanyId == 3);
        settings!.IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task DeactivateTenantAsync_NotFound_ReturnsFalse()
    {
        // Act
        var result = await _service.DeactivateTenantAsync(999);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task DeactivateTenantAsync_AlreadyInactive_StillReturnsTrue()
    {
        // Arrange — already inactive tenant (idempotent operation)
        await SeedCompanyAsync(4);
        await SeedSettingsAsync(4, isProvisioned: true, isActive: false);

        // Act
        var result = await _service.DeactivateTenantAsync(4);

        // Assert — should succeed (idempotent)
        result.ShouldBeTrue();
    }

    #endregion

    #region ProvisionTenantAsync Validation Tests

    [Fact]
    public async Task ProvisionTenantAsync_NoSettings_ThrowsInvalidOperationException()
    {
        // Arrange — company exists but no CompanySystemSettings
        await SeedCompanyAsync(5);

        // Act & Assert — can't provision without settings
        var act = () => _service.ProvisionTenantAsync(5);
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("CompanySystemSettings not found");
    }

    /// <summary>
    /// Issue #192: an already provisioned company is reported as done and NOTHING else
    /// happens. Until this fix the flow logged a warning and then re-ran every step,
    /// including the code table re-seed that deletes rows live documents point at.
    ///
    /// The discriminating assertion is that the call RETURNS: every step past step 1 needs
    /// a reachable PostgreSQL, which unit tests do not have, so the old behaviour could only
    /// ever throw here. The factory assertion backs it up one step further — no data source
    /// for the tenant schema was even requested, i.e. no migration or re-seed was attempted.
    /// The real-database proof that nothing is deleted lives in
    /// Fakvio.Tests.Integration/TenantReprovisioningDatabaseTests.
    /// </summary>
    [Fact]
    public async Task ProvisionTenantAsync_AlreadyProvisioned_ReturnsWithoutTouchingTheTenantSchema()
    {
        // Arrange — company already provisioned
        await SeedCompanyAsync(6);
        await SeedSettingsAsync(6, isProvisioned: true, isActive: true);

        // Act
        var result = await _service.ProvisionTenantAsync(6);

        // Assert — reported as provisioned, and no tenant schema work was started
        result.ShouldBeTrue();
        _dataSourceFactory.DidNotReceive().GetForSchema(Arg.Any<string>(), Arg.Any<bool>());
    }

    /// <summary>
    /// Issue #155 — the provisioning half of the fix relies on this contract: Step 7 now THROWS
    /// when the tenant schema has no active NumberSequenceFormat (see
    /// CreateDefaultNumberSequencesTests), and that throw must prevent Step 8 from marking the
    /// tenant as provisioned. A tenant without number sequences would otherwise look ready while
    /// being unable to number a single document.
    ///
    /// This test pins the mechanism the guard depends on: when ANY step throws, the caller gets
    /// an error naming that step and IsProvisioned stays false. The trigger used here is Step 2
    /// (company is not an issuer), because it is the last failure reachable before the service
    /// opens a PostgreSQL connection — Steps 3+ cannot run in a unit test.
    /// </summary>
    [Fact]
    public async Task ProvisionTenantAsync_WhenAStepFails_LeavesTenantUnprovisionedAndNamesTheStep()
    {
        // Arrange — settings exist (Step 1 passes) but the company is not marked as issuer,
        // so Step 2 fails the same way a Step 7 failure would.
        const long CompanyId = 7;
        var company = await SeedCompanyAsync(CompanyId);
        await SeedSettingsAsync(CompanyId);

        company.IsIssuer = false;
        await _masterContext.SaveChangesAsync();

        // Act
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _service.ProvisionTenantAsync(CompanyId));

        // Assert — the SysAdmin is told which step broke, not just "provisioning failed"
        ex.Message.ShouldContain("Step 2",
            customMessage: "The failing step must be named so the SysAdmin can fix the right thing");

        // Assert — the tenant must NOT be usable. AsNoTracking reads what was really persisted.
        var settings = await _masterContext.CompanySystemSettings.AsNoTracking()
            .FirstAsync(s => s.CompanyId == CompanyId);

        settings.IsProvisioned.ShouldBeFalse(
            customMessage: "A failed step must never leave the tenant marked as provisioned");
        settings.ProvisionedAt.ShouldBeNull();
    }

    #endregion

    #region MigrateAllTenantsAsync Tests

    [Fact]
    public async Task MigrateAllTenantsAsync_NoTenants_ReturnsZero()
    {
        // Arrange — no tenants in the database

        // Act
        var result = await _service.MigrateAllTenantsAsync();

        // Assert
        result.ShouldBe(0);
    }

    [Fact]
    public async Task MigrateAllTenantsAsync_OnlyInactiveTenants_ReturnsZero()
    {
        // Arrange — only inactive tenants exist (should not be migrated)
        await SeedCompanyAsync(7);
        await SeedSettingsAsync(7, isProvisioned: true, isActive: false);

        // Act — MigrateAllTenantsAsync only processes provisioned + active tenants
        // Since we use InMemoryDb, the actual migration will fail, but it filters correctly
        // The method catches per-tenant exceptions, so inactive ones are skipped entirely
        var result = await _service.MigrateAllTenantsAsync();

        // Assert — no active tenants to migrate
        result.ShouldBe(0);
    }

    [Fact]
    public async Task MigrateAllTenantsAsync_UnprovisionedTenants_ReturnsZero()
    {
        // Arrange — only unprovisioned tenants (should not be migrated)
        await SeedCompanyAsync(8);
        await SeedSettingsAsync(8, isProvisioned: false, isActive: true);

        // Act
        var result = await _service.MigrateAllTenantsAsync();

        // Assert — unprovisioned tenants are skipped
        result.ShouldBe(0);
    }

    #endregion

    #region MigrateTenantAsync Tests

    [Fact]
    public async Task MigrateTenantAsync_NotFound_ReturnsFalse()
    {
        // Act — no settings for company 999
        var result = await _service.MigrateTenantAsync(999);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task MigrateTenantAsync_NotProvisioned_ReturnsFalse()
    {
        // Arrange — settings exist but not provisioned
        await SeedCompanyAsync(9);
        await SeedSettingsAsync(9, isProvisioned: false);

        // Act
        var result = await _service.MigrateTenantAsync(9);

        // Assert — can't migrate what doesn't exist
        result.ShouldBeFalse();
    }

    #endregion

    #region CreateTenantContext (per-schema data source)

    /// <summary>
    /// Invokes the private CreateTenantContext(string) via reflection.
    /// The method is private by design (an implementation detail of provisioning), but its
    /// wiring — which data source it uses and where migration history is recorded — is
    /// exactly what this task changed, so it is worth pinning directly.
    /// </summary>
    private TenantDbContext InvokeCreateTenantContext(string schemaName)
    {
        var method = typeof(TenantProvisioningService)
            .GetMethod("CreateTenantContext", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TenantProvisioningService.CreateTenantContext not found.");

        return (TenantDbContext)method.Invoke(_service, [schemaName])!;
    }

    [Fact]
    public void CreateTenantContext_AsksTheFactoryForTheSchemaDataSource_ExactlyOnce()
    {
        // The leak fix: the service must REUSE the factory's cached per-schema data source
        // instead of building (and dropping) a new NpgsqlDataSource on every call.
        // "Exactly once per call" is the observable part of that contract.
        using var context = InvokeCreateTenantContext("tenant_42");

        _dataSourceFactory.Received(1).GetForSchema("tenant_42", true);
    }

    [Fact]
    public void CreateTenantContext_UsesTheDataSourceReturnedByTheFactory()
    {
        // The stubbed schema data source carries a marker in its connection string, so the
        // connection handed out by the context proves it really came from the factory —
        // and not from a locally built data source with a hand-rolled search_path.
        using var context = InvokeCreateTenantContext("tenant_42");

        context.Database.GetDbConnection().ConnectionString.ShouldContain(SchemaDataSourceMarker);
    }

    [Fact]
    public void CreateTenantContext_SanitizesTheSchemaNameBeforeAskingTheFactory()
    {
        // Schema names reach this method from the database (CompanySystemSettings.SchemaName),
        // so they are still sanitized here — the factory sanitizes again, but defence in
        // depth is intentional and the sanitized form must be what gets cached.
        // SchemaNames.Sanitize lowercases and drops everything outside [a-z0-9_].
        using var context = InvokeCreateTenantContext("Tenant_42!");

        _dataSourceFactory.Received(1).GetForSchema("tenant_42", true);
    }

    [Fact]
    public void CreateTenantContext_KeepsMigrationsHistoryTableInTheTenantSchema()
    {
        // Load-bearing regression guard: without a per-schema __EFMigrationsHistory, every
        // tenant would share one history table in "public" and the second tenant would skip
        // migrations that the first one already recorded as applied.
        using var context = InvokeCreateTenantContext("tenant_42");

        // Extensions are keyed by their concrete type, so FindExtension<RelationalOptionsExtension>()
        // would miss the Npgsql-specific subclass — filter the list by assignability instead.
        var relationalOptions = context.GetService<IDbContextOptions>()
            .Extensions.OfType<RelationalOptionsExtension>().Single();

        relationalOptions.MigrationsHistoryTableName.ShouldBe("__EFMigrationsHistory");
        relationalOptions.MigrationsHistoryTableSchema.ShouldBe("tenant_42");
        relationalOptions.MigrationsAssembly.ShouldBe("Fakvio.Infrastructure");
    }

    [Fact]
    public void CreateTenantContext_DisposingTheContext_DoesNotDisposeTheFactoryOwnedDataSource()
    {
        // The ownership rule the whole leak fix rests on: the context BORROWS the factory's
        // per-schema data source, it does not own it. EF Core only disposes a data source it
        // created itself, never one handed to UseNpgsql(DbDataSource) — but that is a
        // third-party guarantee, so pin it: if a future EF/Npgsql version started disposing
        // it, the factory's cache would hand a dead instance to the next tenant and
        // provisioning would fail on everything after the first one.
        using (InvokeCreateTenantContext("tenant_42"))
        {
        }

        IsDisposed(_schemaDataSource).ShouldBeFalse();
    }

    [Fact]
    public void CreateTenantContext_CalledAgainAfterTheFirstContextWasDisposed_GetsTheSameInstanceBack()
    {
        // Provisioning and migration walk tenants in a loop: create context, use it, dispose
        // it, move on. Before the fix every iteration built its own NpgsqlDataSource and
        // dropped it on the floor (one leaked pool per iteration); now every iteration goes
        // back to the factory, which hands out the one cached instance per schema.
        using (InvokeCreateTenantContext("tenant_42"))
        {
        }

        using var second = InvokeCreateTenantContext("tenant_42");

        _dataSourceFactory.Received(2).GetForSchema("tenant_42", true);
        DataSourceOf(second).ShouldBeSameAs(_schemaDataSource);
    }

    /// <summary>
    /// Returns the data source EF Core actually stored in the context's options — the
    /// strongest available proof of "which instance is this context running on", stronger
    /// than comparing connection strings.
    /// </summary>
    private static DbDataSource? DataSourceOf(DbContext context) =>
        context.GetService<IDbContextOptions>()
            .FindExtension<NpgsqlOptionsExtension>()?.DataSource;

    /// <summary>
    /// Reads Npgsql's private disposal flag. NpgsqlDataSource exposes no public "is disposed"
    /// state, and its only member that reacts to disposal (OpenConnection) would attempt a
    /// real network connection when the source is still alive — which a unit test must not do.
    /// </summary>
    private static bool IsDisposed(NpgsqlDataSource dataSource)
    {
        var field = typeof(NpgsqlDataSource)
            .GetField("_isDisposed", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "NpgsqlDataSource._isDisposed not found — Npgsql internals changed, adjust this helper.");

        return (int)field.GetValue(dataSource)! != 0;
    }

    #endregion

    public void Dispose()
    {
        _masterContext.Database.EnsureDeleted();
        _masterContext.Dispose();
        _rootDataSource.Dispose();
        _schemaDataSource.Dispose();
    }
}
