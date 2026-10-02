using System.Globalization;
using Fakvio.Application.Service;
using Microsoft.Extensions.Caching.Memory;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// ECB reference rates via the SDMX REST API (https://data-api.ecb.europa.eu/). Typed HttpClient
/// registered with AddHttpClient; results are cached because a past day's rate never changes.
/// </summary>
public class EcbExchangeRateClient : IEcbExchangeRateClient
{
    // How many calendar days AFTER the requested date we look for the next published rate
    // (weekends + TARGET holidays have no fixing).
    private const int LookaheadDays = 10;

    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;

    public EcbExchangeRateClient(HttpClient http, IMemoryCache cache)
    {
        _http = http;
        _cache = cache;
    }

    public async Task<decimal> GetUnitsPerEurAsync(string currencyCode, DateOnly date, CancellationToken ct = default)
    {
        var code = currencyCode.Trim().ToUpperInvariant();
        if (code == "EUR") return 1m;

        // Currency code goes into the URL path — allow only 3 ASCII letters (no injection into the request).
        if (code.Length != 3 || !code.All(c => c is >= 'A' and <= 'Z'))
            throw new EcbRateUnavailableException($"Invalid currency code '{currencyCode}'.");

        var key = $"ecb:{code}:{date:yyyyMMdd}";
        if (_cache.TryGetValue(key, out decimal cached)) return cached;

        // Art. 369h(2) VAT directive / §110zb ZDPH: the rate of the LAST DAY of the period, or — when the ECB
        // published none that day — the rate of the NEXT publication day. So we look forward, never back.
        var to = date.AddDays(LookaheadDays);
        var url = $"https://data-api.ecb.europa.eu/service/data/EXR/D.{code}.EUR.SP00.A" +
                  $"?startPeriod={date:yyyy-MM-dd}&endPeriod={to:yyyy-MM-dd}&format=csvdata";

        string csv;
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                throw new EcbRateUnavailableException(
                    $"ECB returned HTTP {(int)response.StatusCode} for {code} on {date:yyyy-MM-dd}.");
            csv = await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            throw new EcbRateUnavailableException($"ECB exchange rate service is unreachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new EcbRateUnavailableException("ECB exchange rate service timed out.", ex);
        }

        // No observation yet (e.g. the quarter just ended on a weekend and the next fixing is not published)
        // is an error, never a fallback to an earlier day — and it is NOT cached, so a retry can succeed.
        var rate = ParseFirstObservationOnOrAfter(csv, date)
            ?? throw new EcbRateUnavailableException(
                $"ECB has not published a {code} rate for {date:yyyy-MM-dd} or the following days yet. Try again later.");

        _cache.Set(key, rate, TimeSpan.FromDays(1)); // only an exactly found rate is cached
        return rate;
    }

    /// <summary>
    /// Parses the SDMX csvdata body (header row with TIME_PERIOD and OBS_VALUE columns) and returns
    /// the OBS_VALUE of the EARLIEST TIME_PERIOD that is on or after <paramref name="date"/>, or null when
    /// there is none. Public for unit tests.
    /// </summary>
    public static decimal? ParseFirstObservationOnOrAfter(string csv, DateOnly date)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2) return null;

        var header = lines[0].Split(',');
        var timeIdx = Array.IndexOf(header, "TIME_PERIOD");
        var valueIdx = Array.IndexOf(header, "OBS_VALUE");
        if (timeIdx < 0 || valueIdx < 0) return null;

        var minTime = date.ToString("yyyy-MM-dd");
        string? bestTime = null;
        decimal? best = null;
        foreach (var line in lines.Skip(1))
        {
            // Dataset keys contain no commas/quotes for EXR series, so a plain split is enough.
            var cols = line.Split(',');
            if (cols.Length <= Math.Max(timeIdx, valueIdx)) continue;
            if (!decimal.TryParse(cols[valueIdx], NumberStyles.Number, CultureInfo.InvariantCulture, out var v) || v <= 0) continue;
            // yyyy-MM-dd sorts lexicographically.
            if (string.CompareOrdinal(cols[timeIdx], minTime) < 0) continue;
            if (bestTime == null || string.CompareOrdinal(cols[timeIdx], bestTime) < 0)
            {
                bestTime = cols[timeIdx];
                best = v;
            }
        }
        return best;
    }
}
