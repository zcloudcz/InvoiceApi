using System.Collections.Concurrent;
using Fakvio.Application.Service;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Single source of truth for tenant schema resolution and migration.
///
/// This factory handles:
/// 1. ResolveSchemaAsync — CompanyId → MasterDb lookup → validation → schema name
/// 2. EnsureMigratedAsync — lazy migration (once per schema per process lifetime)
/// 3. CreateContextAsync / CreateContextForCompanyAsync — full context creation
///
/// Both API and Functions middleware call ResolveSchemaAsync + EnsureMigratedAsync
/// instead of duplicating the MasterDb lookup. This eliminates code duplication
/// and keeps tenant resolution logic in one place.
///
/// Junior note: Before this refactor, each middleware (API, Functions) had its own
/// copy of the "CompanyId → MasterDb query → validate → schema" logic. Now they
/// just call factory.ResolveSchemaAsync(companyId) and get the schema name back.
/// </summary>
public class TenantDbContextFactory : ITenantDbContextFactory
{
    private readonly ITenantResolver _tenantResolver;
    private readonly MasterDbContext _masterDb;
    private readonly ITenantProvisioningService _provisioningService;
    private readonly IConfiguration _configuration;
    private readonly ICurrentUserService? _currentUserService;
    private readonly ILogger<TenantDbContextFactory> _logger;

    /// <summary>
    /// Static cache of schemas that have been migrated in this process lifetime.
    /// Key = schema name (e.g., "tenant_42"), Value = true (migrated).
    ///
    /// Static because migration status is per-process, not per-scope.
    /// A schema only needs to be migrated once after deployment — all subsequent
    /// requests skip the check instantly via TryGetValue.
    ///
    /// Junior note: ConcurrentDictionary is thread-safe — multiple requests can
    /// check/add entries simultaneously without locks or race conditions.
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> _migratedSchemas = new();

    /// <summary>
    /// Per-schema semaphore to prevent concurrent migrations of the same schema.
    /// Without this, two concurrent requests for the same tenant could both trigger
    /// MigrateAsync at the same time, causing conflicts.
    ///
    /// Junior note: SemaphoreSlim(1,1) acts like an async lock — only one caller
    /// can enter at a time. We use one semaphore per schema so different tenants
    /// can migrate in parallel, but the same tenant is serialized.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _migrationLocks = new();

    public TenantDbContextFactory(
        ITenantResolver tenantResolver,
        MasterDbContext masterDb,
        ITenantProvisioningService provisioningService,
        IConfiguration configuration,
        ILogger<TenantDbContextFactory> logger,
        ICurrentUserService? currentUserService = null)
    {
        _tenantResolver = tenantResolver;
        _masterDb = masterDb;
        _provisioningService = provisioningService;
        _configuration = configuration;
        _logger = logger;
        _currentUserService = currentUserService;
    }

    /// <inheritdoc />
    public async Task<string?> ResolveSchemaAsync(long companyId, CancellationToken cancellationToken = default)
    {
        // Single place for CompanyId → schema resolution.
        // Both API and Functions middleware call this instead of doing their own MasterDb lookup.
        var settings = await _masterDb.CompanySystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, cancellationToken);

        if (settings == null)
        {
            _logger.LogWarning("No CompanySystemSettings for CompanyId {CompanyId}", companyId);
            return null;
        }

        if (!settings.IsProvisioned)
        {
            _logger.LogWarning("CompanyId {CompanyId} is not provisioned", companyId);
            return null;
        }

        if (!settings.IsActive)
        {
            _logger.LogWarning("CompanyId {CompanyId} is inactive", companyId);
            return null;
        }

        return settings.SchemaName;
    }

    /// <inheritdoc />
    public async Task EnsureMigratedAsync(long companyId, CancellationToken cancellationToken = default)
    {
        // Resolve schema name first (uses the same MasterDb lookup as ResolveSchemaAsync).
        var settings = await _masterDb.CompanySystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId && s.IsProvisioned && s.IsActive, cancellationToken);

        if (settings == null)
            return; // Not provisioned or not active — nothing to migrate.

        var schemaName = settings.SchemaName;

        // Fast path: already migrated in this process lifetime → skip.
        if (_migratedSchemas.ContainsKey(schemaName))
            return;

        // Slow path: first request for this schema since process start.
        // Use a per-schema semaphore so only one migration runs at a time,
        // but different tenants can migrate concurrently.
        var semaphore = _migrationLocks.GetOrAdd(schemaName, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);

        try
        {
            // Double-check after acquiring the lock — another request may have migrated
            // while we were waiting.
            if (_migratedSchemas.ContainsKey(schemaName))
                return;

            _logger.LogInformation("Ensuring tenant schema '{SchemaName}' is migrated (first request after startup)",
                schemaName);

            // Delegate to TenantProvisioningService — it has the proper CreateTenantContext
            // with search_path and per-schema __EFMigrationsHistory.
            await _provisioningService.MigrateTenantAsync(settings.CompanyId, cancellationToken);

            // Mark as migrated — all subsequent requests for this schema skip instantly.
            _migratedSchemas.TryAdd(schemaName, true);

            _logger.LogInformation("Tenant schema '{SchemaName}' migrated successfully", schemaName);
        }
        catch (Exception ex)
        {
            // Log but don't crash — the tenant may already be up-to-date,
            // or the error may be transient. Let the actual query reveal real issues.
            _logger.LogError(ex, "Failed to migrate tenant schema '{SchemaName}'", schemaName);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<DbContext> CreateContextAsync(CancellationToken cancellationToken = default)
    {
        var companyId = _tenantResolver.GetCurrentCompanyId();

        if (!companyId.HasValue)
        {
            throw new InvalidOperationException(
                "Cannot create tenant context: no CompanyId available. " +
                "SysAdmin users must use X-Company-Id header to specify which company to access.");
        }

        return await CreateContextForCompanyAsync(companyId.Value, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DbContext> CreateContextForCompanyAsync(long companyId, CancellationToken cancellationToken = default)
    {
        var schemaName = await ResolveSchemaAsync(companyId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Cannot create tenant context for CompanyId {companyId}: " +
                "not found, not provisioned, or inactive.");

        // Ensure migrations are applied before returning the context.
        await EnsureMigratedAsync(companyId, cancellationToken);

        _logger.LogDebug("Creating TenantDbContext for CompanyId {CompanyId}, Schema: {SchemaName}",
            companyId, schemaName);

        return CreateTenantContext(schemaName);
    }

    /// <inheritdoc />
    public async Task<string?> GetConnectionStringAsync(long companyId, CancellationToken cancellationToken = default)
    {
        var settings = await _masterDb.CompanySystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, cancellationToken);

        return settings == null ? null : GetConnectionString();
    }

    /// <summary>
    /// Creates a TenantDbContext instance configured for a specific schema.
    ///
    /// The context uses the shared PostgreSQL connection string and sets the Schema
    /// property so that HasDefaultSchema() in OnModelCreating routes all tables
    /// to the correct tenant schema.
    /// </summary>
    public TenantDbContext CreateTenantContext(string schemaName)
    {
        var connectionString = GetConnectionString();

        var optionsBuilder = new DbContextOptionsBuilder<TenantDbContext>();
        optionsBuilder.UseNpgsql(connectionString, b =>
        {
            b.MigrationsAssembly("Fakvio.Infrastructure");
            b.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorCodesToAdd: null);
        });

        // Custom model cache: one cached model per schema (tenant_42, tenant_99, etc.)
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>();

        var context = _currentUserService != null
            ? new TenantDbContext(optionsBuilder.Options, _currentUserService)
            : new TenantDbContext(optionsBuilder.Options);

        context.Schema = schemaName;
        return context;
    }

    /// <inheritdoc />
    /// <summary>
    /// Creates a lightweight TenantDbContext (returned as DbContext) for a known schema WITHOUT
    /// performing any migration checks. Used by background services (e.g., ExchangeRateRefreshService)
    /// that iterate all tenant schemas and need a disposable context per tenant.
    /// The caller can safely cast the result to TenantDbContext when needed.
    /// </summary>
    public DbContext CreateForSchema(string schemaName) => CreateTenantContext(schemaName);

    /// <summary>
    /// Gets the shared PostgreSQL connection string from configuration.
    /// In multi-schema architecture, all tenants share the same database connection.
    /// </summary>
    private string GetConnectionString()
    {
        return _configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "DefaultConnection not found in configuration. " +
                "Cannot create tenant context.");
    }
}
