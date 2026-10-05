using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.ExchangeRate;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// ČNB exchange rates: Master-DB table first, ČNB download on a miss (DEVGUIDE §4.17).
/// </summary>
public class ExchangeRateService : IExchangeRateService
{
    // A stored row older than this (relative to the requested date) is not trusted to be "the last fixing
    // on or before the date" when ČNB cannot be asked — the sync may have missed days.
    private const int MaxStaleDays = 4;

    private readonly MasterDbContext _context;
    private readonly ICnbExchangeRateClient _client;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ExchangeRateService> _logger;

    public ExchangeRateService(MasterDbContext context, ICnbExchangeRateClient client, IMemoryCache cache,
        ILogger<ExchangeRateService> logger)
    {
        _context = context;
        _client = client;
        _cache = cache;
        _logger = logger;
    }

    public async Task<ExchangeRateDto?> GetRateAsync(string currencyCode, DateOnly date, CancellationToken ct = default)
    {
        var code = (currencyCode ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length != 3 || code == "CZK" || !code.All(c => c is >= 'A' and <= 'Z'))
            return null;

        // A past day's answer never changes, so it is cached; today/future may still change (fixing ~14:30).
        var cacheable = date < DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var key = $"cnb:{code}:{date:yyyyMMdd}";
        if (cacheable && _cache.TryGetValue(key, out ExchangeRateDto? cached)) return cached;

        var best = await FindStoredAsync(code, date, ct);

        // Exact day stored = definitely the right fixing. Otherwise (weekend, holiday, missing day, not yet
        // published) ask ČNB which fixing is the last one on or before the date.
        var trustworthy = best?.ValidFor == date;
        if (!trustworthy)
        {
            try
            {
                var validFor = await FetchAndStoreAsync(date, ct);
                best = await FindStoredAsync(code, validFor, ct) ?? best;
                trustworthy = true; // ČNB answered: best is the real "last fixing on or before date"
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "ČNB rate lookup for {Currency} on {Date} failed; using stored data if recent enough", code, date);
                if (best != null && best.ValidFor < date.AddDays(-MaxStaleDays)) best = null;
            }
        }

        var dto = best is null ? null : ToDto(best);
        // Never cache an answer produced by the outage fallback — it may be older than the real fixing.
        if (cacheable && dto != null && trustworthy) _cache.Set(key, dto, TimeSpan.FromHours(12));
        return dto;
    }

    private Task<ExchangeRate?> FindStoredAsync(string code, DateOnly date, CancellationToken ct) =>
        _context.ExchangeRate.AsNoTracking()
            .Where(r => r.CurrencyCode == code && r.ValidFor <= date)
            .OrderByDescending(r => r.ValidFor)
            .FirstOrDefaultAsync(ct);

    public async Task<DateOnly> FetchAndStoreAsync(DateOnly date, CancellationToken ct = default)
    {
        var daily = await _client.GetDailyRatesAsync(date, ct);

        var existing = await _context.ExchangeRate
            .Where(r => r.ValidFor == daily.ValidFor)
            .ToDictionaryAsync(r => r.CurrencyCode, ct);

        foreach (var row in daily.Rows)
        {
            if (existing.TryGetValue(row.CurrencyCode, out var entity))
            {
                // Published fixings do not change; only touch the row if ČNB corrected it.
                entity.Rate = row.Rate;
                entity.Amount = row.Amount;
            }
            else
            {
                _context.ExchangeRate.Add(new ExchangeRate
                {
                    CurrencyCode = row.CurrencyCode,
                    Amount = row.Amount,
                    Rate = row.Rate,
                    ValidFor = daily.ValidFor
                });
            }
        }

        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // Only a unique violation is swallowed; any other DB error propagates.
            // Another instance/request inserted the same (currency, date) rows first (unique index) — the
            // data is there, which is all we wanted. Detach our failed attempt so the context stays usable.
            _logger.LogInformation(ex, "ČNB rates for {ValidFor} were stored concurrently", daily.ValidFor);
            _context.ChangeTracker.Clear();
        }

        return daily.ValidFor;
    }

    private static ExchangeRateDto ToDto(ExchangeRate r) => new()
    {
        CurrencyCode = r.CurrencyCode,
        Amount = r.Amount,
        Rate = r.Rate,
        RatePerUnit = r.Rate / r.Amount,
        ValidFor = r.ValidFor
    };
}
