namespace Fakvio.Application.Service;

/// <summary>
/// Result returned by a single refresh cycle so callers can log the outcome
/// without duplicating the skip/run decision logic.
/// </summary>
public record ExchangeRateRefreshResult(
    /// <summary>True when the cycle was skipped (interval not elapsed, mode = Off, etc.).</summary>
    bool Skipped,
    /// <summary>Human-readable reason for skipping. Null when the cycle actually ran.</summary>
    string? SkipReason,
    /// <summary>Number of new exchange rate records saved. Zero when skipped.</summary>
    int SavedCount);

/// <summary>
/// Stateless service that performs one exchange rate refresh cycle.
///
/// Following the "API + Functions duplication" pattern from CLAUDE.md:
/// <list type="bullet">
///   <item><see cref="IExchangeRateRefreshService"/> — pure logic, no scheduling state.</item>
///   <item><c>ExchangeRateRefreshWorker</c> (BackgroundService) — drives the cycle in the API host.</item>
///   <item><c>ExchangeRateFunctions.RefreshRates</c> (TimerTrigger) — drives the cycle in Azure Functions.</item>
/// </list>
///
/// The service uses a PostgreSQL advisory lock so that simultaneous invocations
/// (e.g., API worker + Functions on the same database) do not double-import.
///
/// The service is per-tenant — each tenant has its own ExchangeRate table in its schema.
/// The cycle iterates all provisioned tenants and refreshes each one independently.
/// </summary>
public interface IExchangeRateRefreshService
{
    /// <summary>
    /// Performs one refresh cycle for all provisioned tenants.
    /// Skips tenants whose <c>ExchangeRateUpdateMode</c> is <c>Off</c> or whose
    /// configured interval has not elapsed since <c>ExchangeRateLastRunAt</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ExchangeRateRefreshResult> RunCycleAsync(CancellationToken cancellationToken = default);
}
