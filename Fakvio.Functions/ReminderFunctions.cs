using Fakvio.Application.Service;
using Fakvio.Infrastructure.Data;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fakvio.Functions;

/// <summary>
/// Timer trigger function for the daily dunning (payment reminder) job.
///
/// Runs daily at 6:00 AM UTC. For each active, provisioned tenant:
///   1. Creates a scoped DI container with the tenant's TenantDbContext.
///   2. Resolves IReminderService within that scope.
///   3. Calls ProcessOverdueInvoicesAsync() which scans overdue invoices and creates/sends reminders.
///
/// Why 6 AM UTC?
///   Most Czech businesses start at 8 AM CET (= 7 AM UTC in summer, 6 AM UTC in winter).
///   Running at 6 AM UTC ensures reminders are created and emails sent before the workday
///   starts in both CET and CEST timezones.
///
/// Multi-tenant iteration pattern:
///   Same approach as LogCleanup — iterates CompanySystemSettings from the master schema
///   and creates per-tenant scopes. Each tenant is processed independently; one tenant's
///   failure does not affect others.
/// </summary>
public class ReminderFunctions
{
    private readonly IServiceProvider _serviceProvider;
    private readonly MasterDbContext _masterContext;
    private readonly ILogger<ReminderFunctions> _logger;

    public ReminderFunctions(
        IServiceProvider serviceProvider,
        MasterDbContext masterContext,
        ILogger<ReminderFunctions> logger)
    {
        _serviceProvider = serviceProvider;
        _masterContext = masterContext;
        _logger = logger;
    }

    /// <summary>
    /// Daily dunning job — processes overdue invoices across all active tenants.
    ///
    /// CRON: "0 0 6 * * *" = every day at 06:00:00 UTC.
    /// Azure Functions CRON format: {second} {minute} {hour} {day} {month} {dayOfWeek}
    /// </summary>
    [Function("ProcessReminders")]
    public async Task ProcessReminders(
        [TimerTrigger("0 0 6 * * *")] TimerInfo timer)
    {
        _logger.LogInformation("ProcessReminders: Starting daily dunning job");

        // 1. Get all active, provisioned tenants from the master database.
        var tenants = await _masterContext.CompanySystemSettings
            .AsNoTracking()
            .Where(s => s.IsProvisioned && s.IsActive)
            .ToListAsync();

        _logger.LogInformation("ProcessReminders: Found {TenantCount} active tenants", tenants.Count);

        var totalReminders = 0;
        var tenantsProcessed = 0;

        foreach (var tenant in tenants)
        {
            try
            {
                // 2. Create a scoped DI container for this tenant.
                using var scope = _serviceProvider.CreateScope();

                // 3. Resolve and configure TenantDbContext with the tenant's schema.
                //    CreateContextForCompanyAsync sets Schema on the scoped TenantDbContext
                //    so all subsequent queries within this scope use the tenant's schema.
                var factory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
                await factory.CreateContextForCompanyAsync(tenant.CompanyId);

                // 4. Resolve IReminderService within the tenant scope.
                var reminderService = scope.ServiceProvider.GetRequiredService<IReminderService>();

                // 5. Process overdue invoices for this tenant.
                var count = await reminderService.ProcessOverdueInvoicesAsync();

                if (count > 0)
                {
                    _logger.LogInformation(
                        "ProcessReminders: Tenant {CompanyId} ({SchemaName}): created {Count} reminders",
                        tenant.CompanyId, tenant.SchemaName, count);
                }

                totalReminders += count;
                tenantsProcessed++;
            }
            catch (Exception ex)
            {
                // One tenant's failure should not stop the entire job.
                _logger.LogError(ex,
                    "ProcessReminders: Error processing tenant {CompanyId} ({SchemaName})",
                    tenant.CompanyId, tenant.SchemaName);
            }
        }

        _logger.LogInformation(
            "ProcessReminders: Done. Created {TotalReminders} reminders across {TenantsProcessed}/{TotalTenants} tenants",
            totalReminders, tenantsProcessed, tenants.Count);
    }
}
