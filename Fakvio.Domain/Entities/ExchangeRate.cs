using Fakvio.Domain.Common;

namespace Fakvio.Domain.Entities;

/// <summary>
/// One official ČNB (Czech National Bank) daily fixing for one currency — "kurzy devizového trhu".
/// Rows are exactly as ČNB publishes them: <see cref="Rate"/> is CZK for <see cref="Amount"/> units
/// of the currency (e.g. 100 HUF = 6.660 CZK), so the per-unit rate is <c>Rate / Amount</c>.
///
/// Lives ONLY in the Master database (no tenant copy) — it is public reference data every tenant
/// reads identically (DEVGUIDE §11.2, §4.17). Filled by ExchangeRateSyncService (daily worker +
/// backfill) and, on a cache miss, by ExchangeRateService itself.
///
/// This is the rate for Czech VAT (§38 ZDPH). EU OSS uses ECB rates instead (EcbExchangeRateClient).
/// </summary>
public class ExchangeRate : BaseEntity
{
    /// <summary>ISO 4217 code, upper case (e.g. "EUR").</summary>
    public string CurrencyCode { get; set; } = string.Empty;

    /// <summary>How many units of the currency the <see cref="Rate"/> is quoted for (1, 100 or 1000 in ČNB data).</summary>
    public int Amount { get; set; } = 1;

    /// <summary>CZK for <see cref="Amount"/> units of the currency, as published (3 decimals).</summary>
    public decimal Rate { get; set; }

    /// <summary>
    /// The date the fixing was declared for (the date in the first line of the ČNB file). A request
    /// for a weekend/holiday gets the last published fixing, so this is NOT necessarily the requested date.
    /// </summary>
    public DateOnly ValidFor { get; set; }
}
