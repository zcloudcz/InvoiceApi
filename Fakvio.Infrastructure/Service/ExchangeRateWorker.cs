using Fakvio.Application.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// BackgroundService wrapper for the ČNB exchange rate download (DEVGUIDE §6.1/§6.3, §4.17).
/// Runs once at startup (catches up after downtime / fills a fresh DB), then every day at 14:45
/// Prague time — ČNB declares the fixing at about 14:30 on Czech business days. A failed cycle
/// (ČNB down) is retried after 30 minutes. Master-only data, so unlike the tenant workers it has
/// no per-tenant loop. Advisory lock = one instance downloads at a time.
/// </summary>
public class ExchangeRateWorker : BackgroundService
{
    /// <summary>Advisory lock key — "FAKVIOFX" as ASCII. Registered in DEVGUIDE §6.3.</summary>
    private const long AdvisoryLockKey = 0x46414B56494F4658L;

    internal static readonly TimeSpan RunAt = new(14, 45, 0);
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExchangeRateWorker> _logger;

    public ExchangeRateWorker(IServiceScopeFactory scopeFactory, ILogger<ExchangeRateWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ExchangeRateWorker started, daily at {RunAt} Prague time", RunAt);

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                await RunLockedAsync(stoppingToken);
                delay = DelayUntilNextRun(PragueNow());
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ExchangeRateWorker cycle failed; retrying in {Delay}", RetryDelay);
                delay = RetryDelay;
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("ExchangeRateWorker stopped");
    }

    private async Task RunLockedAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var lockHandle = await AdvisoryLock.TryAcquireAsync(dataSource, AdvisoryLockKey, ct);
        if (lockHandle is null)
        {
            _logger.LogDebug("ExchangeRateWorker: another instance holds the lock, skipping this run");
            return;
        }

        await scope.ServiceProvider.GetRequiredService<IExchangeRateSyncService>().RunCycleAsync(ct);
    }

    /// <summary>Time from <paramref name="pragueNow"/> to the next 14:45 Prague (tomorrow's when today's has passed). Internal for tests.</summary>
    internal static TimeSpan DelayUntilNextRun(DateTimeOffset pragueNow)
    {
        var next = pragueNow.Date + RunAt;           // wall-clock 14:45 today
        if (next <= pragueNow.DateTime) next = next.AddDays(1);
        return next - pragueNow.DateTime;            // DST change days can be off by an hour — harmless, cycle is idempotent
    }

    private static readonly TimeZoneInfo Prague = FindPrague();

    private static TimeZoneInfo FindPrague()
    {
        // IANA id works on Linux and (ICU) on Windows; the Windows id is the fallback for older hosts.
        foreach (var id in new[] { "Europe/Prague", "Central Europe Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
        }
        return TimeZoneInfo.Utc; // ponytail: UTC fallback shifts the run by 1-2 h; harmless, the cycle is idempotent
    }

    /// <summary>Current time in Prague (offset-aware; <c>.DateTime</c> is the wall clock).</summary>
    internal static DateTimeOffset PragueNow() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Prague);

    /// <summary>Today's calendar date in Prague.</summary>
    internal static DateOnly PragueToday() => DateOnly.FromDateTime(PragueNow().DateTime);
}
