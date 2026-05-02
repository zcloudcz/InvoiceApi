using Microsoft.EntityFrameworkCore;

namespace Fakvio.Application.Service;

/// <summary>
/// Factory that creates DbContext instances connected to the correct tenant database
/// and ensures the tenant schema is migrated before use.
///
/// This is the SINGLE source of truth for tenant resolution and migration:
/// 1. ResolveSchemaAsync — CompanyId → MasterDb lookup → validates provisioned/active → returns schema name
/// 2. EnsureMigratedAsync — lazily applies EF Core migrations (once per schema per process lifetime)
/// 3. CreateContextAsync / CreateContextForCompanyAsync — full context creation
///
/// Both API and Functions middleware call ResolveSchemaAsync + EnsureMigratedAsync
/// instead of duplicating the MasterDb lookup logic.
///
/// Junior note: "Single source of truth" means there is only ONE place in the code
/// that knows how to resolve a CompanyId to a schema name. If we need to change
/// the resolution logic, we change it here — not in 3 different middleware files.
/// </summary>
public interface ITenantDbContextFactory
{
    /// <summary>
    /// Resolves the tenant schema name for a given CompanyId.
    ///
    /// This is the CORE resolution method used by all middleware and services.
    /// Does: MasterDb lookup → validates IsProvisioned + IsActive → returns schema name.
    ///
    /// Returns null if:
    /// - CompanySystemSettings not found for the given companyId
    /// - Company is not provisioned
    /// - Company is not active
    ///
    /// Junior note: Returns null instead of throwing so middleware can decide
    /// how to handle invalid tenants (403, skip, etc.) without try/catch.
    /// </summary>
    Task<string?> ResolveSchemaAsync(long companyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures the tenant schema has been migrated (all EF Core migrations applied).
    ///
    /// Uses an in-memory cache — migration runs only ONCE per schema per process lifetime.
    /// First request after deployment triggers the migration; subsequent requests are instant.
    ///
    /// Safe for concurrent calls — only one migration runs at a time per schema.
    ///
    /// Junior note: "Lazy migration" means we don't migrate all tenants at startup.
    /// Instead, each tenant's schema is migrated on first use. This is faster at startup
    /// and handles new tenants that are provisioned while the app is running.
    /// </summary>
    Task EnsureMigratedAsync(long companyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a TenantDbContext for the current request's company (from JWT CompanyId claim).
    /// Uses ITenantResolver internally to determine the CompanyId.
    ///
    /// Throws if:
    /// - No CompanyId in the current request (SysAdmin without impersonation)
    /// - Company is not provisioned (database not yet created)
    /// - Company is inactive (suspended by SysAdmin)
    /// </summary>
    Task<DbContext> CreateContextAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a TenantDbContext for a specific company by ID.
    /// Used by SysAdmin tools, provisioning service, and background jobs.
    ///
    /// Throws if:
    /// - CompanySystemSettings not found for the given companyId
    /// - Company is not provisioned
    /// - Company is inactive
    /// </summary>
    Task<DbContext> CreateContextForCompanyAsync(long companyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the connection string for a specific tenant database WITHOUT creating a context.
    /// Used by TenantProvisioningService to get the target DB connection for migration.
    /// Returns null if CompanySystemSettings not found.
    /// </summary>
    Task<string?> GetConnectionStringAsync(long companyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a <see cref="DbContext"/> (TenantDbContext cast) for a known schema name
    /// WITHOUT performing any migration checks.
    /// Used by background services (ExchangeRateRefreshService) that iterate all tenant schemas
    /// from the master DB and need a lightweight, disposable context per schema.
    ///
    /// The caller is responsible for disposing the returned context.
    /// Migrations must have been applied to the schema before calling this method.
    /// </summary>
    DbContext CreateForSchema(string schemaName);
}
