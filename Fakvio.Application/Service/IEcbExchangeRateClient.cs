namespace Fakvio.Application.Service;

/// <summary>
/// Fetches official European Central Bank reference exchange rates
/// (https://data-api.ecb.europa.eu/, dataset EXR — "Euro foreign exchange reference rates").
///
/// Used by the EU OSS quarterly report (OssReportService) to convert non-EUR invoice
/// totals to EUR using the rate of the LAST DAY OF THE REPORTED QUARTER, per OSS filing
/// rules (the OSS return itself is always filed in EUR regardless of invoice currency).
/// This is intentionally a different source than <see cref="ICurrencyService.ConvertToCzkAsync"/>,
/// which uses the Czech National Bank (ČNB) rate for the EPO CZ VAT return — OSS and EPO
/// report in different target currencies and are legally separate filings.
/// </summary>
public interface IEcbExchangeRateClient
{
    /// <summary>
    /// Returns how many units of <paramref name="currencyCode"/> equal 1 EUR on
    /// <paramref name="date"/> (the ECB's own convention — e.g. ~25.30 for CZK).
    /// Returns 1 for "EUR" without calling the API.
    ///
    /// ECB does not publish rates on weekends/EU holidays. Per Art. 369h(2) of the VAT directive
    /// (§110zb ZDPH) the rate of <paramref name="date"/> is used, or — if none was published that
    /// day — the rate of the NEXT publication day (looking up to 10 days ahead). Never an earlier day.
    /// </summary>
    /// <exception cref="EcbRateUnavailableException">
    /// The ECB API could not be reached, or no observation on/after the date has been published (yet).
    /// </exception>
    Task<decimal> GetUnitsPerEurAsync(string currencyCode, DateOnly date, CancellationToken ct = default);
}

/// <summary>
/// Thrown when an ECB exchange rate could not be obtained — either the API is unreachable,
/// or it returned no observation for the requested currency within the lookback window.
/// The OSS report cannot be generated without a rate (silently defaulting to 1:1 would
/// misstate VAT owed), so this is a hard failure, not a logged-and-ignored fallback.
/// </summary>
public class EcbRateUnavailableException : Exception
{
    public EcbRateUnavailableException(string message) : base(message) { }
    public EcbRateUnavailableException(string message, Exception inner) : base(message, inner) { }
}
