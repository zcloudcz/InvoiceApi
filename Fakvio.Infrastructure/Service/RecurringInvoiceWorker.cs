using Fakvio.Application.Service;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Hourly recurring-invoice generation job, hosted as a <see cref="BackgroundService"/>.
///
/// Every hour, for each active, provisioned tenant:
///   1. Creates a scoped DI container with the tenant's TenantDbContext.
///   2. Resolves IRecurringInvoiceService within that scope.
///   3. Calls RunCycleAsync(companyId, utcNow) which generates due invoices (DEVGUIDE §4.13).
///
/// Why hourly (not daily like ReminderWorker)? Schedules can fire at any time of day the user
/// picked (e.g. "1st of the month at 9 AM") — an hourly tick keeps the generation close enough
/// to the planned time without the cost of a tighter poll. Missed periods (an outage spanning
/// the tick) still catch up correctly — see RecurringInvoiceService.RunCycleAsync docs.
///
/// Multi-tenant iteration + per-tenant error isolation: identical pattern to ReminderWorker —
/// one tenant's failure does not stop the others, and a PostgreSQL advisory lock
/// (<see cref="AdvisoryLockKey"/>) guarantees only one App Service replica runs a cycle at a time.
///
/// Requires "Always On" on the App Service — see DEVGUIDE §6.
/// </summary>
public class RecurringInvoiceWorker : BackgroundService
{
    /// <summary>How often the worker checks for due schedules.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    /// <summary>Advisory lock key — "FAKVIORI" as ASCII. Registered in DEVGUIDE §6.3.</summary>
    private const long AdvisoryLockKey = 0x46414B56494F5249L;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RecurringInvoiceWorker> _logger;

    public RecurringInvoiceWorker(IServiceScopeFactory scopeFactory, ILogger<RecurringInvoiceWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Main loop — never exits until the process is stopped.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RecurringInvoiceWorker started, polling every {Interval}", PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunLockedAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failure reading the tenant list must not kill the loop — next tick may succeed.
                _logger.LogError(ex, "RecurringInvoiceWorker cycle failed");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("RecurringInvoiceWorker stopped");
    }

    /// <summary>Takes the cross-instance advisory lock, then runs one pass.</summary>
    private async Task RunLockedAsync(CancellationToken ct)
    {
        using var lockScope = _scopeFactory.CreateScope();
        var dataSource = lockScope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var lockHandle = await AdvisoryLock.TryAcquireAsync(dataSource, AdvisoryLockKey, ct);
        if (lockHandle is null)
        {
            _logger.LogInformation("RecurringInvoice: another instance holds the lock, skipping this run");
            return;
        }

        await RunOnceAsync(ct);
    }

    /// <summary>
    /// One pass over every active, provisioned tenant. Internal so the unit test can drive a
    /// cycle without waiting an hour and without a PostgreSQL lock.
    /// </summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        _logger.LogInformation("RecurringInvoice: Starting generation cycle");

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

        var totalGenerated = 0;
        var tenantsProcessed = 0;

        foreach (var (companyId, schemaName) in tenants)
        {
            try
            {
                using var scope = await _scopeFactory.CreateTenantScopeAsync(companyId, ct);

                var recurringInvoiceService = scope.ServiceProvider.GetRequiredService<IRecurringInvoiceService>();
                var count = await recurringInvoiceService.RunCycleAsync(companyId, nowUtc, ct);

                if (count > 0)
                {
                    _logger.LogInformation(
                        "RecurringInvoice: Tenant {CompanyId} ({SchemaName}): generated {Count} invoices",
                        companyId, schemaName, count);
                }

                totalGenerated += count;
                tenantsProcessed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One tenant's failure must not stop the entire cycle.
                _logger.LogError(ex,
                    "RecurringInvoice: Error processing tenant {CompanyId} ({SchemaName})",
                    companyId, schemaName);
            }
        }

        _logger.LogInformation(
            "RecurringInvoice: Done. Generated {TotalGenerated} invoices across {TenantsProcessed}/{TotalTenants} tenants",
            totalGenerated, tenantsProcessed, tenants.Count);
    }
}
