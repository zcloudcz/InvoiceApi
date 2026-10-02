using Fakvio.Contracts.Dto.ExchangeRate;

namespace Fakvio.Application.Service;

/// <summary>
/// Official ČNB exchange rates for Czech VAT (§38 ZDPH; §29 ZDPH requires the VAT amount in CZK).
/// Master-DB cache of the daily fixing, with an on-demand fetch from ČNB on a cache miss.
/// (EU OSS legally uses ECB rates — see <see cref="IEcbExchangeRateClient"/>; the two are separate.)
/// </summary>
public interface IExchangeRateService
{
    /// <summary>
    /// The rate valid on <paramref name="date"/>: the last ČNB fixing declared on or before it
    /// (a weekend/holiday therefore gets Friday's rate). Returns null for CZK, for an unknown/invalid
    /// currency code, or when ČNB cannot be reached and nothing is cached — never a made-up rate.
    /// </summary>
    Task<ExchangeRateDto?> GetRateAsync(string currencyCode, DateOnly date, CancellationToken ct = default);

    /// <summary>
    /// Downloads the fixing ČNB reports for <paramref name="date"/> and upserts it (idempotent).
    /// Returns the date the fixing was declared for. Throws when ČNB is unreachable or sends an unparsable file.
    /// </summary>
    Task<DateOnly> FetchAndStoreAsync(DateOnly date, CancellationToken ct = default);
}

/// <summary>
/// One cycle of the daily rate download (DEVGUIDE §6): stateless, called by ExchangeRateWorker
/// and by the SysAdmin "backfill" endpoint.
/// </summary>
public interface IExchangeRateSyncService
{
    /// <summary>
    /// Fetches every day from the day after the newest stored fixing (at most 30 days back) up to
    /// today (Prague time). Idempotent — safe to run any number of times.
    /// </summary>
    /// <returns>Number of daily files downloaded.</returns>
    Task<int> RunCycleAsync(CancellationToken ct = default);

    /// <summary>Downloads every day in [from, to] (inclusive, max 400 days). Returns the number of files downloaded.</summary>
    Task<int> BackfillAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
}

/// <summary>Raw access to the ČNB daily fixing file (typed HttpClient; faked in tests).</summary>
public interface ICnbExchangeRateClient
{
    /// <summary>Downloads and parses the fixing ČNB returns for <paramref name="date"/> (the last one declared on/before it).</summary>
    /// <exception cref="InvalidOperationException">ČNB unreachable, HTTP error or unparsable body.</exception>
    Task<CnbDailyRates> GetDailyRatesAsync(DateOnly date, CancellationToken ct = default);
}

/// <summary>Parsed ČNB daily fixing: the declaration date and one row per currency.</summary>
public record CnbDailyRates(DateOnly ValidFor, IReadOnlyList<CnbRateRow> Rows);

/// <summary>One line of the ČNB file: <paramref name="Rate"/> CZK for <paramref name="Amount"/> units.</summary>
public record CnbRateRow(string CurrencyCode, int Amount, decimal Rate);
