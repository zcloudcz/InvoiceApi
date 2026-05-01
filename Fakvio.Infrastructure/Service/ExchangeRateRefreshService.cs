using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Stateless service that performs one exchange rate refresh cycle for all provisioned tenants.
///
/// Following the "API + Functions duplication" pattern from CLAUDE.md:
/// <list type="bullet">
///   <item>This service contains ALL cycle logic — no scheduling state, no Thread.Sleep.</item>
///   <item><see cref="ExchangeRateRefreshWorker"/> (BackgroundService) calls it in the API host.</item>
///   <item><c>ExchangeRateFunctions.RefreshRates</c> (TimerTrigger) calls it in Azure Functions.</item>
/// </list>
///
/// The service uses an advisory lock so concurrent invocations (e.g., API worker + Functions)
/// do not double-import. Each tenant is processed independently; one tenant's failure does
/// not abort other tenants.
/// </summary>
public class ExchangeRateRefreshService : IExchangeRateRefreshService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IExchangeRateProvider _cnbProvider;
    private readonly ILogger<ExchangeRateRefreshService> _logger;

    // Advisory lock key — unique per application to avoid collisions with other advisory locks
    private const long AdvisoryLockKey = 9_360_000_001L; // arbitrary stable number for exchange rate refresh

    public ExchangeRateRefreshService(
        IServiceScopeFactory scopeFactory,
        IExchangeRateProvider cnbProvider,
        ILogger<ExchangeRateRefreshService> logger)
    {
        _scopeFactory = scopeFactory;
        _cnbProvider = cnbProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ExchangeRateRefreshResult> RunCycleAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("ExchangeRateRefreshService: starting cycle");

        // We need a scoped context for the master DB to list provisioned tenants.
        using var scope = _scopeFactory.CreateScope();
        var masterContext = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var tenantFactory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();

        // Load all provisioned + active tenants with their exchange rate settings.
        var tenants = await masterContext.CompanySystemSettings
            .AsNoTracking()
            .Where(s => s.IsProvisioned && s.IsActive)
            .ToListAsync(cancellationToken);

        if (tenants.Count == 0)
        {
            _logger.LogDebug("ExchangeRateRefreshService: no provisioned tenants — skipping");
            return new ExchangeRateRefreshResult(Skipped: true, SkipReason: "No provisioned tenants", SavedCount: 0);
        }

        int totalSaved = 0;
        int processedTenants = 0;

        foreach (var tenant in tenants)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            // Skip tenants that have disabled auto-refresh
            if (tenant.ExchangeRateUpdateMode == EExchangeRateUpdateMode.Off)
                continue;

            // Check whether the configured interval has elapsed since the last run
            if (!ShouldRefresh(tenant))
                continue;

            try
            {
                var saved = await RefreshForTenantAsync(tenant, masterContext, tenantFactory, cancellationToken);
                totalSaved += saved;
                processedTenants++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One tenant's failure must not abort other tenants
                _logger.LogError(ex,
                    "ExchangeRateRefreshService: failed to refresh rates for tenant schema {Schema}",
                    tenant.SchemaName);
            }
        }

        _logger.LogInformation(
            "ExchangeRateRefreshService: cycle complete — processed {Tenants} tenants, saved {Saved} rate records",
            processedTenants, totalSaved);

        return new ExchangeRateRefreshResult(Skipped: false, SkipReason: null, SavedCount: totalSaved);
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the tenant's configured refresh interval has elapsed
    /// since <see cref="CompanySystemSettings.ExchangeRateLastRunAt"/>.
    /// </summary>
    private bool ShouldRefresh(CompanySystemSettings settings)
    {
        var now = DateTime.UtcNow;

        if (settings.ExchangeRateLastRunAt == null)
            return true; // Never ran — always run on first opportunity

        var lastRun = settings.ExchangeRateLastRunAt.Value;

        return settings.ExchangeRateUpdateMode switch
        {
            // Daily: run if more than 20 hours since last run (gives 4h grace around CNB publish time)
            EExchangeRateUpdateMode.Daily => (now - lastRun) >= TimeSpan.FromHours(20),

            // Weekly: run if more than 6 days have elapsed AND today matches the configured day
            EExchangeRateUpdateMode.Weekly => (now - lastRun) >= TimeSpan.FromDays(6)
                && DateTime.UtcNow.DayOfWeek == (settings.ExchangeRateUpdateDayOfWeek ?? DayOfWeek.Monday),

            // Monthly: run if more than 25 days have elapsed (catches the 1st of each month)
            EExchangeRateUpdateMode.Monthly => (now - lastRun) >= TimeSpan.FromDays(25),

            // Off and unknown: skip
            _ => false
        };
    }

    /// <summary>
    /// Fetches today's CNB rates and upserts them into the tenant's ExchangeRate table.
    /// Updates <see cref="CompanySystemSettings.ExchangeRateLastRunAt"/> on success.
    /// </summary>
    /// <returns>Number of new records inserted.</returns>
    private async Task<int> RefreshForTenantAsync(
        CompanySystemSettings tenantSettings,
        MasterDbContext masterContext,
        ITenantDbContextFactory tenantFactory,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "ExchangeRateRefreshService: refreshing rates for tenant {Schema}",
            tenantSettings.SchemaName);

        // Fetch rates from CNB
        var rates = await _cnbProvider.FetchRatesAsync(null, cancellationToken);

        if (rates.Count == 0)
        {
            _logger.LogWarning(
                "ExchangeRateRefreshService: CNB returned 0 rates for tenant {Schema} — skipping upsert",
                tenantSettings.SchemaName);
            return 0;
        }

        // Open a scoped TenantDbContext for this specific schema.
        // CreateForSchema returns DbContext (interface contract), but the concrete type
        // is TenantDbContext — safe to cast here in Infrastructure layer.
        using var baseContext = tenantFactory.CreateForSchema(tenantSettings.SchemaName);
        var tenantContext = (TenantDbContext)baseContext;

        int saved = await UpsertRatesAsync(tenantContext, rates, cancellationToken);

        // Mark the last run timestamp in master DB
        // We must use a tracked instance to update — load it fresh within this scope
        var trackedSettings = await masterContext.CompanySystemSettings
            .FirstAsync(s => s.Id == tenantSettings.Id, cancellationToken);

        trackedSettings.ExchangeRateLastRunAt = DateTime.UtcNow;
        await masterContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "ExchangeRateRefreshService: saved {Count} rate records for tenant {Schema}",
            saved, tenantSettings.SchemaName);

        return saved;
    }

    /// <summary>
    /// Inserts new exchange rate records, skipping any that already exist for the
    /// same (CurrencyCode, ValidFrom) combination (idempotent import).
    /// </summary>
    private static async Task<int> UpsertRatesAsync(
        TenantDbContext context,
        List<ExchangeRate> rates,
        CancellationToken cancellationToken)
    {
        // Determine which (code, date) pairs are already in the database to avoid duplicates.
        // We use a date-only comparison: if a record with the same ValidFrom already exists,
        // we consider this date imported and skip — CNB rates don't change intra-day.
        var today = rates.Select(r => r.ValidFrom).Distinct().ToList();

        var existing = await context.ExchangeRate
            .Where(r => today.Contains(r.ValidFrom))
            .Select(r => new { r.CurrencyCode, r.ValidFrom })
            .ToListAsync(cancellationToken);

        var existingSet = existing
            .Select(e => (e.CurrencyCode, e.ValidFrom))
            .ToHashSet();

        var newRates = rates
            .Where(r => !existingSet.Contains((r.CurrencyCode, r.ValidFrom)))
            .ToList();

        if (newRates.Count == 0)
            return 0;

        context.ExchangeRate.AddRange(newRates);
        await context.SaveChangesAsync(cancellationToken);

        return newRates.Count;
    }
}
