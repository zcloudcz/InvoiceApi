using InvoiceApi.Application.Dto.Client;

namespace InvoiceApi.Application.Service;

/// <summary>
/// Service for managing the lifecycle of tenant databases in the multi-tenant architecture.
///
/// Provisioning flow:
/// 1. SysAdmin creates a Company (Client with IsIssuer = true) in master DB
/// 2. SysAdmin triggers provisioning via POST /api/company/{id}/provision
/// 3. TenantProvisioningService:
///    a) Creates the PostgreSQL database
///    b) Applies TenantDbContext migrations (schema creation)
///    c) Copies code tables from master DB (VatRate, Currency, NumberSequenceFormat, ContentTemplate)
///    d) Creates the issuer (company) record in the tenant DB
///    e) Creates default number sequences for Invoice and CreditNote
///    f) Marks CompanySystemSettings.IsProvisioned = true with ProvisionedAt timestamp
///
/// Deprovisioning does NOT delete the database — it only marks it as inactive.
/// Actual database deletion is a manual DBA operation (safety measure).
/// </summary>
public interface ITenantProvisioningService
{
    /// <summary>
    /// Provisions a new tenant database for the given company.
    /// Creates the database, applies migrations, copies code tables, and creates the issuer.
    /// Throws if the company is already provisioned or if provisioning fails.
    /// </summary>
    /// <param name="companyId">The master DB company ID to provision</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if provisioning succeeded</returns>
    Task<bool> ProvisionTenantAsync(long companyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Activates a previously deactivated tenant.
    /// Does NOT re-provision — the database must already exist.
    /// </summary>
    /// <param name="companyId">The master DB company ID to activate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if activation succeeded, false if not found</returns>
    Task<bool> ActivateTenantAsync(long companyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deactivates a tenant. The database remains but all access is blocked.
    /// Users belonging to this company will not be able to log in to tenant features.
    /// </summary>
    /// <param name="companyId">The master DB company ID to deactivate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deactivation succeeded, false if not found</returns>
    Task<bool> DeactivateTenantAsync(long companyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies pending EF Core migrations to a specific tenant database.
    /// Used during startup auto-migration and manual maintenance.
    /// </summary>
    /// <param name="companyId">The master DB company ID whose tenant DB to migrate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if migration succeeded</returns>
    Task<bool> MigrateTenantAsync(long companyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies pending migrations to all provisioned and active tenant databases.
    /// Used at application startup to keep all tenant schemas up to date.
    /// Continues even if individual tenants fail (logs errors).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of successfully migrated tenants</returns>
    Task<int> MigrateAllTenantsAsync(CancellationToken cancellationToken = default);
    Task DeleteAllTenantDbs(CancellationToken cancellationToken = default);
}
