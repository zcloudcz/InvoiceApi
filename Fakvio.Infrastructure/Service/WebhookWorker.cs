using Fakvio.Application.Service;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// BackgroundService wrapper for the webhook dispatch cycle (DEVGUIDE §6.1/§6.3). Every tick,
/// for each active, provisioned tenant: opens a scoped TenantDbContext for that company and
/// calls <see cref="IWebhookDispatchService.RunCycleAsync"/>. Same shape as RecurringInvoiceWorker.
///
/// No Fakvio.Functions counterpart — the only host is Fakvio.API (no Functions project exists
/// in this repo today; see DEVGUIDE §6).
/// </summary>
public class WebhookWorker : BackgroundService
{
    /// <summary>Short interval — retries and new deliveries should go out promptly, not sit for an hour.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    /// <summary>Advisory lock key — "FAKVIOWH" as ASCII. Registered in DEVGUIDE §6.3.</summary>
    private const long AdvisoryLockKey = 0x46414B56494F5748L;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WebhookWorker> _logger;

    public WebhookWorker(IServiceScopeFactory scopeFactory, ILogger<WebhookWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WebhookWorker started, polling every {Interval}", PollInterval);

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
                _logger.LogError(ex, "WebhookWorker cycle failed");
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

        _logger.LogInformation("WebhookWorker stopped");
    }

    private async Task RunLockedAsync(CancellationToken ct)
    {
        using var lockScope = _scopeFactory.CreateScope();
        var dataSource = lockScope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var lockHandle = await AdvisoryLock.TryAcquireAsync(dataSource, AdvisoryLockKey, ct);
        if (lockHandle is null)
        {
            _logger.LogDebug("WebhookWorker: another instance holds the lock, skipping this run");
            return;
        }

        await RunOnceAsync(ct);
    }

    /// <summary>One pass over every active, provisioned tenant. Internal so tests can drive a cycle directly.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
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

        foreach (var (companyId, schemaName) in tenants)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var factory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
                await factory.CreateContextForCompanyAsync(companyId, ct);

                var dispatchService = scope.ServiceProvider.GetRequiredService<IWebhookDispatchService>();
                await dispatchService.RunCycleAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WebhookWorker: Error processing tenant {CompanyId} ({SchemaName})", companyId, schemaName);
            }
        }
    }
}
