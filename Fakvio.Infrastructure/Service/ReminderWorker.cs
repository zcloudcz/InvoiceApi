using Fakvio.Application.Service;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Daily dunning (payment reminder) job, hosted as a <see cref="BackgroundService"/>.
///
/// Runs once a day at <see cref="RunAtUtc"/>. For each active, provisioned tenant:
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
///   Same approach as LogCleanupService — iterates CompanySystemSettings from the master schema
///   and creates per-tenant scopes. Each tenant is processed independently; one tenant's
///   failure does not affect others.
///
/// Requires "Always On" on the App Service: without it the platform recycles an idle process
/// and the 06:00 tick is missed.
/// </summary>
public class ReminderWorker : BackgroundService
{
    /// <summary>Time of day (UTC) the job runs at.</summary>
    internal static readonly TimeSpan RunAtUtc = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReminderWorker> _logger;

    public ReminderWorker(IServiceScopeFactory scopeFactory, ILogger<ReminderWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// How long to sleep from <paramref name="nowUtc"/> until the next <paramref name="runAtUtc"/>.
    /// Today's slot if it is still ahead, otherwise tomorrow's. Exactly on the slot counts as
    /// "still ahead" being false — the job would have fired a moment ago, so wait for tomorrow.
    /// </summary>
    internal static TimeSpan DelayUntil(DateTime nowUtc, TimeSpan runAtUtc)
    {
        var next = nowUtc.Date + runAtUtc;
        if (next <= nowUtc)
        {
            next = next.AddDays(1);
        }
        return next - nowUtc;
    }

    /// <summary>Main loop — never exits until the process is stopped.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ReminderWorker started, next run at {RunAtUtc:hh\\:mm} UTC", RunAtUtc);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(DelayUntil(DateTime.UtcNow, RunAtUtc), stoppingToken);
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failure reading the tenant list must not kill the loop — tomorrow's run may succeed.
                _logger.LogError(ex, "ReminderWorker cycle failed");
            }
        }

        _logger.LogInformation("ReminderWorker stopped");
    }

    /// <summary>
    /// One pass over every active, provisioned tenant. Internal so the unit test can drive a
    /// cycle without waiting for 06:00.
    /// </summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        _logger.LogInformation("ProcessReminders: Starting daily dunning job");

        // 1. Get all active, provisioned tenants from the master database.
        List<(long CompanyId, string SchemaName)> tenants;
        using (var masterScope = _scopeFactory.CreateScope())
        {
            var masterContext = masterScope.ServiceProvider.GetRequiredService<MasterDbContext>();
            tenants = (await masterContext.CompanySystemSettings
                    .AsNoTracking()
                    .Where(s => s.IsProvisioned && s.IsActive)
                    .Select(s => new { s.CompanyId, s.SchemaName })
                    .ToListAsync(ct))
                .Select(s => (s.CompanyId, s.SchemaName))
                .ToList();
        }

        _logger.LogInformation("ProcessReminders: Found {TenantCount} active tenants", tenants.Count);

        var totalReminders = 0;
        var tenantsProcessed = 0;

        foreach (var (companyId, schemaName) in tenants)
        {
            try
            {
                // 2. Create a scoped DI container for this tenant.
                using var scope = _scopeFactory.CreateScope();

                // 3. CreateContextForCompanyAsync sets Schema on the scoped TenantDbContext
                //    so all subsequent queries within this scope use the tenant's schema.
                var factory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
                await factory.CreateContextForCompanyAsync(companyId, ct);

                // 4. Resolve IReminderService within the tenant scope and process overdue invoices.
                var reminderService = scope.ServiceProvider.GetRequiredService<IReminderService>();
                var count = await reminderService.ProcessOverdueInvoicesAsync(ct);

                if (count > 0)
                {
                    _logger.LogInformation(
                        "ProcessReminders: Tenant {CompanyId} ({SchemaName}): created {Count} reminders",
                        companyId, schemaName, count);
                }

                totalReminders += count;
                tenantsProcessed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One tenant's failure should not stop the entire job.
                _logger.LogError(ex,
                    "ProcessReminders: Error processing tenant {CompanyId} ({SchemaName})",
                    companyId, schemaName);
            }
        }

        _logger.LogInformation(
            "ProcessReminders: Done. Created {TotalReminders} reminders across {TenantsProcessed}/{TotalTenants} tenants",
            totalReminders, tenantsProcessed, tenants.Count);
    }
}
