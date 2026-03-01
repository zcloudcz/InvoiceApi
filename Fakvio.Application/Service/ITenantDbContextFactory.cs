using Microsoft.EntityFrameworkCore;

namespace Fakvio.Application.Service;

/// <summary>
/// Factory that creates DbContext instances connected to the correct tenant database.
///
/// How it works:
/// 1. ITenantResolver provides the CompanyId from the current HTTP request (JWT claim)
/// 2. This factory looks up CompanySystemSettings in the master DB for that CompanyId
/// 3. CompanySystemSettings has the DatabaseName (or full ConnectionString override)
/// 4. Factory builds a connection string and creates a TenantDbContext pointing to that DB
///
/// Two creation methods:
/// - CreateContextAsync() — for the current request (uses ITenantResolver to get CompanyId)
/// - CreateContextForCompanyAsync(companyId) — for explicit company ID (used by SysAdmin tools,
///   provisioning, background jobs where HTTP context may not be available)
///
/// Returns DbContext (not TenantDbContext directly) because this interface lives in the
/// Application layer which doesn't reference Infrastructure. The actual implementation
/// returns TenantDbContext — callers in Infrastructure/API cast or use DI typing.
///
/// Throws:
/// - InvalidOperationException if no CompanyId is available
/// - InvalidOperationException if company is not provisioned (no database yet)
/// - InvalidOperationException if company is inactive (suspended/deactivated)
/// </summary>
public interface ITenantDbContextFactory
{
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
}
