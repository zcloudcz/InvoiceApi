using Fakvio.Application.Service;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Fakvio.Functions;

/// <summary>
/// Azure Functions timer trigger for automatic CNB exchange rate refresh.
///
/// Why a Function alongside the BackgroundService in the API project?
/// Azure Functions Isolated Worker does NOT host BackgroundService reliably
/// (it would only run while a function is actively executing, and the consumption
/// plan can scale to zero between invocations). For Azure-hosted production deploys
/// we therefore drive the refresh from a TimerTrigger here. The same
/// <see cref="IExchangeRateRefreshService"/> is the single source of truth for the cycle logic.
///
/// Scheduling strategy:
///   - <see cref="RefreshRates"/> fires every 30 minutes (CRON "0 */30 * * * *").
///   - The cycle itself checks CompanySystemSettings.ExchangeRateUpdateMode and
///     ExchangeRateLastRunAt to decide whether the configured interval has elapsed.
///   - This gives us per-tenant configurable cadence (Off/Daily/Weekly/Monthly)
///     without redeploying the Function.
///
/// See CLAUDE.md "API + Functions duplication" for the full deployment story.
/// </summary>
public class ExchangeRateFunctions
{
    private readonly IExchangeRateRefreshService _refreshService;
    private readonly ILogger<ExchangeRateFunctions> _logger;

    public ExchangeRateFunctions(
        IExchangeRateRefreshService refreshService,
        ILogger<ExchangeRateFunctions> logger)
    {
        _refreshService = refreshService;
        _logger = logger;
    }

    /// <summary>
    /// Runs the exchange rate refresh cycle every 30 minutes.
    /// The service itself determines whether the configured interval has elapsed
    /// for each tenant — most invocations will be no-ops for a given tenant.
    ///
    /// CRON format: {second} {minute} {hour} {day} {month} {dayOfWeek}
    /// "0 */30 * * * *" = every 30 minutes, on the minute.
    /// </summary>
    [Function("RefreshExchangeRates")]
    public async Task RefreshRates(
        [TimerTrigger("0 */30 * * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        _logger.LogInformation("ExchangeRateFunctions.RefreshRates triggered");

        var result = await _refreshService.RunCycleAsync(ct);

        if (result.Skipped)
        {
            _logger.LogDebug("RefreshExchangeRates: skipped — {Reason}", result.SkipReason);
        }
        else
        {
            _logger.LogInformation(
                "RefreshExchangeRates: cycle complete, saved {Count} rate records", result.SavedCount);
        }
    }
}
