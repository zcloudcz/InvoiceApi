namespace Fakvio.Contracts.Dto.Currency;

/// <summary>
/// Read DTO for a single exchange rate record (CNB daily rate).
/// </summary>
public class ExchangeRateDto
{
    /// <summary>Database primary key.</summary>
    public long Id { get; set; }

    /// <summary>ISO 4217 currency code (e.g., "EUR", "USD").</summary>
    public string CurrencyCode { get; set; } = string.Empty;

    /// <summary>CNB publication date — the date this rate is valid for.</summary>
    public DateOnly ValidFrom { get; set; }

    /// <summary>
    /// CZK amount for <see cref="Amount"/> units of the foreign currency.
    /// Divide Rate / Amount to get the per-unit rate.
    /// </summary>
    public decimal Rate { get; set; }

    /// <summary>CNB unit count (usually 1; can be 100 for JPY, HUF, etc.).</summary>
    public int Amount { get; set; }

    /// <summary>Source of the rate (e.g., "CNB").</summary>
    public string Source { get; set; } = "CNB";

    /// <summary>Effective per-unit rate in CZK: Rate / Amount.</summary>
    public decimal EffectiveRate => Amount > 0 ? Rate / Amount : 0m;
}
