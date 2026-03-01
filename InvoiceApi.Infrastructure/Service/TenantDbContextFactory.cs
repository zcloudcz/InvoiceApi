using InvoiceApi.Application.Service;
using InvoiceApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace InvoiceApi.Infrastructure.Service;

/// <summary>
/// Creates TenantDbContext instances connected to the correct tenant schema.
///
/// Architecture: Single PostgreSQL database with schema-per-tenant isolation.
/// All tenants share the same database connection; isolation is via PostgreSQL schemas.
///
/// Flow:
/// 1. Get CompanyId (from ITenantResolver or explicit parameter)
/// 2. Look up CompanySystemSettings in master DB by CompanyId
/// 3. Validate: is provisioned? is active?
/// 4. Create TenantDbContext with the Schema property set to settings.SchemaName
///
/// The TenantDbContext.Schema property drives HasDefaultSchema() in OnModelCreating,
/// which prefixes all table names with the schema (e.g., "tenant_42"."Invoice").
/// TenantModelCacheKeyFactory ensures EF Core caches a separate compiled model per schema.
/// </summary>
public class TenantDbContextFactory : ITenantDbContextFactory
{
    private readonly ITenantResolver _tenantResolver;
    private readonly MasterDbContext _masterDb;
    private readonly IConfiguration _configuration;
    private readonly ICurrentUserService? _currentUserService;
    private readonly ILogger<TenantDbContextFactory> _logger;

    public TenantDbContextFactory(
        ITenantResolver tenantResolver,
        MasterDbContext masterDb,
        IConfiguration configuration,
        ILogger<TenantDbContextFactory> logger,
        ICurrentUserService? currentUserService = null)
    {
        _tenantResolver = tenantResolver;
        _masterDb = masterDb;
        _configuration = configuration;
        _logger = logger;
        _currentUserService = currentUserService;
    }

    /// <inheritdoc />
    public async Task<DbContext> CreateContextAsync(CancellationToken cancellationToken = default)
    {
        // Get the CompanyId from the current HTTP request (JWT claim or impersonation)
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
        // Look up the tenant's infrastructure settings in the master database
        var settings = await _masterDb.CompanySystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, cancellationToken);

        if (settings == null)
        {
            throw new InvalidOperationException(
                $"No CompanySystemSettings found for CompanyId {companyId}. " +
                "The company may not exist or hasn't been registered for multi-tenancy.");
        }

        // Validate the tenant is provisioned (schema exists and has been migrated)
        if (!settings.IsProvisioned)
        {
            throw new InvalidOperationException(
                $"Tenant schema for CompanyId {companyId} has not been provisioned yet. " +
                "A SysAdmin must provision the company before it can be accessed.");
        }

        // Validate the tenant is active (not suspended/deactivated)
        if (!settings.IsActive)
        {
            throw new InvalidOperationException(
                $"Tenant for CompanyId {companyId} is currently inactive (suspended). " +
                "Contact your system administrator.");
        }

        _logger.LogDebug(
            "Creating TenantDbContext for CompanyId {CompanyId}, Schema: {SchemaName}",
            companyId, settings.SchemaName);

        // Create TenantDbContext connected to the shared PostgreSQL database
        // with the Schema property set to route queries to the correct tenant schema.
        return CreateTenantContext(settings.SchemaName);
    }

    /// <inheritdoc />
    public async Task<string?> GetConnectionStringAsync(long companyId, CancellationToken cancellationToken = default)
    {
        // In multi-schema architecture, all tenants share the same connection string.
        // This method returns the shared connection string if the tenant exists.
        var settings = await _masterDb.CompanySystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, cancellationToken);

        if (settings == null)
        {
            return null;
        }

        return GetConnectionString();
    }

    /// <summary>
    /// Creates a TenantDbContext instance configured for a specific schema.
    /// Used by both runtime tenant resolution and provisioning operations.
    ///
    /// The context uses the shared PostgreSQL connection string and sets the Schema
    /// property so that HasDefaultSchema() in OnModelCreating routes all tables
    /// to the correct tenant schema.
    /// </summary>
    /// <param name="schemaName">PostgreSQL schema name (e.g., "tenant_42")</param>
    /// <returns>TenantDbContext configured for the specified schema</returns>
    public TenantDbContext CreateTenantContext(string schemaName)
    {
        var connectionString = GetConnectionString();

        var optionsBuilder = new DbContextOptionsBuilder<TenantDbContext>();
        optionsBuilder.UseNpgsql(connectionString, b =>
        {
            b.MigrationsAssembly("InvoiceApi.Infrastructure");
            b.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorCodesToAdd: null);
        });

        // Register the custom model cache key factory so each schema gets its own cached model.
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>();

        var context = _currentUserService != null
            ? new TenantDbContext(optionsBuilder.Options, _currentUserService)
            : new TenantDbContext(optionsBuilder.Options);

        // Set the schema — TenantDbContext.OnModelCreating uses this for HasDefaultSchema()
        context.Schema = schemaName;

        return context;
    }

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
