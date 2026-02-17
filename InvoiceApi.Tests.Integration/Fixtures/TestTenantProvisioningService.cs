using InvoiceApi.Application.Service;
using InvoiceApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace InvoiceApi.Tests.Integration.Fixtures;

/// <summary>
/// Test double for ITenantProvisioningService used in integration tests.
///
/// The real TenantProvisioningService creates actual SQL Server databases, applies
/// EF Core migrations, and copies code tables — all operations that require a real
/// SQL Server instance and are incompatible with InMemoryDatabase.
///
/// This test double:
/// - ProvisionTenantAsync: marks CompanySystemSettings as provisioned + active (no real DB creation)
/// - ActivateTenantAsync / DeactivateTenantAsync: toggles the IsActive flag
/// - MigrateTenantAsync / MigrateAllTenantsAsync: no-op (InMemory doesn't have migrations)
/// - DeleteAllTenantDbs: no-op
///
/// This allows integration tests to exercise the full HTTP pipeline (controllers,
/// middleware, auth) without needing a real database server.
/// </summary>
public class TestTenantProvisioningService : ITenantProvisioningService
{
    private readonly MasterDbContext _masterContext;

    public TestTenantProvisioningService(MasterDbContext masterContext)
    {
        _masterContext = masterContext;
    }

    /// <summary>
    /// Simulates tenant provisioning by marking the CompanySystemSettings as provisioned and active.
    /// Does NOT create a real database — in tests, all tenants share the same InMemoryDatabase.
    /// </summary>
    public async Task<bool> ProvisionTenantAsync(long companyId, CancellationToken cancellationToken = default)
    {
        // Find the CompanySystemSettings record in the master DB
        var settings = await _masterContext.CompanySystemSettings
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, cancellationToken);

        if (settings == null)
            throw new InvalidOperationException(
                $"CompanySystemSettings not found for company {companyId}. " +
                "Create settings first via POST /api/company/settings.");

        if (settings.IsProvisioned)
            throw new InvalidOperationException(
                $"Company {companyId} is already provisioned.");

        // Mark as provisioned and active — this is all the test needs
        settings.IsProvisioned = true;
        settings.IsActive = true;
        settings.ProvisionedAt = DateTime.UtcNow;

        await _masterContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Activates a previously deactivated tenant by setting IsActive = true.
    /// </summary>
    public async Task<bool> ActivateTenantAsync(long companyId, CancellationToken cancellationToken = default)
    {
        var settings = await _masterContext.CompanySystemSettings
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, cancellationToken);

        if (settings == null)
            return false;

        settings.IsActive = true;
        await _masterContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Deactivates a tenant by setting IsActive = false.
    /// The TenantContextMiddleware will return 403 for this company's endpoints.
    /// </summary>
    public async Task<bool> DeactivateTenantAsync(long companyId, CancellationToken cancellationToken = default)
    {
        var settings = await _masterContext.CompanySystemSettings
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, cancellationToken);

        if (settings == null)
            return false;

        settings.IsActive = false;
        await _masterContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// No-op in tests — InMemoryDatabase doesn't support migrations.
    /// </summary>
    public Task<bool> MigrateTenantAsync(long companyId, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    /// <summary>
    /// No-op in tests — InMemoryDatabase doesn't support migrations.
    /// Returns 0 (no tenants migrated).
    /// </summary>
    public Task<int> MigrateAllTenantsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    /// <summary>
    /// No-op in tests — there are no real databases to delete.
    /// </summary>
    public Task DeleteAllTenantDbs(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
