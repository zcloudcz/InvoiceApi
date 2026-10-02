using Fakvio.Application.Service;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>Stateless one-cycle ČNB rate download (DEVGUIDE §6.1). The cycle logic lives only here.</summary>
public class ExchangeRateSyncService : IExchangeRateSyncService
{
    private const int MaxCatchUpDays = 30;
    private const int MaxBackfillDays = 400;

    private readonly IExchangeRateService _rates;
    private readonly MasterDbContext _context;
    private readonly ILogger<ExchangeRateSyncService> _logger;

    public ExchangeRateSyncService(IExchangeRateService rates, MasterDbContext context, ILogger<ExchangeRateSyncService> logger)
    {
        _rates = rates;
        _context = context;
        _logger = logger;
    }

    public async Task<int> RunCycleAsync(CancellationToken ct = default)
    {
        var today = ExchangeRateWorker.PragueToday();
        var newest = await _context.ExchangeRate.MaxAsync(r => (DateOnly?)r.ValidFor, ct);

        // Start at the newest stored fixing (again: today's may have been published after the last run),
        // but never more than 30 days back — a fresh DB gets 30 days of history.
        var earliest = today.AddDays(-MaxCatchUpDays);
        var from = newest is null || newest.Value < earliest ? earliest : newest.Value;
        return await BackfillAsync(from, today, ct);
    }

    public async Task<int> BackfillAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (to < from) throw new ArgumentException("'to' must not be before 'from'.");
        if (to.DayNumber - from.DayNumber > MaxBackfillDays)
            throw new ArgumentException($"At most {MaxBackfillDays} days can be backfilled at once.");

        var downloaded = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();
            // Weekends have no fixing (ČNB would just repeat Friday) — skip them to save requests.
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            await _rates.FetchAndStoreAsync(d, ct);
            downloaded++;
        }
        _logger.LogInformation("ČNB rate sync: {Count} daily file(s) processed for {From}..{To}", downloaded, from, to);
        return downloaded;
    }
}
