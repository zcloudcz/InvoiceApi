using Fakvio.Application.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// BackgroundService wrapper around <see cref="IExchangeRateRefreshService"/> for hosts
/// that support <see cref="IHostedService"/> (the API project, classic VM deploy, local dev).
///
/// IMPORTANT: Azure Functions Isolated Worker does NOT host BackgroundServices reliably,
/// so for Functions deploys the same <see cref="IExchangeRateRefreshService"/> is invoked
/// from <c>ExchangeRateFunctions.RefreshRates</c> via [TimerTrigger].
/// See CLAUDE.md → "API + Functions duplication" for context.
///
/// Interval: the worker polls every 30 minutes. The service itself checks
/// whether the tenant's configured interval has elapsed and skips early invocations.
/// This keeps the loop simple while allowing SysAdmin-controlled cadence.
/// </summary>
public class ExchangeRateRefreshWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExchangeRateRefreshWorker> _logger;

    // How often the worker wakes up to check if any tenant needs a refresh.
    // The actual refresh cadence is driven by CompanySystemSettings.ExchangeRateUpdateMode.
    private static readonly TimeSpan PollingInterval = TimeSpan.FromMinutes(30);

    public ExchangeRateRefreshWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<ExchangeRateRefreshWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Main loop — polls every 30 minutes until the process is stopped.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ExchangeRateRefreshWorker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Resolve the service from a new scope — IExchangeRateRefreshService is scoped
                using var scope = _scopeFactory.CreateScope();
                var refreshService = scope.ServiceProvider.GetRequiredService<IExchangeRateRefreshService>();
                await refreshService.RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Log but don't crash — the loop continues on the next interval
                _logger.LogError(ex, "ExchangeRateRefreshWorker cycle threw an unexpected exception");
            }

            try
            {
                await Task.Delay(PollingInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("ExchangeRateRefreshWorker stopped");
    }
}
