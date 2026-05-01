using Fakvio.Domain.Common;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Exchange rate record from CNB (Czech National Bank) daily FX list.
/// Stores the official rate for a given currency on a specific date.
///
/// Calculation: to convert <c>amount</c> in <c>CurrencyCode</c> to CZK:
/// <code>czk = amount * (Rate / Amount)</code>
/// where <c>Amount</c> is the unit count stated by CNB (e.g., 100 for JPY, 1 for EUR).
///
/// ValidFrom corresponds to the CNB publication date (working days only).
/// To find the rate for a date that falls on a weekend or public holiday,
/// use the most recent record with ValidFrom &lt;= target date.
/// </summary>
public class ExchangeRate : BaseEntity
{
    /// <summary>
    /// ISO 4217 currency code (e.g., "EUR", "USD").
    /// "CZK" is never stored — it is always 1:1.
    /// </summary>
    public string CurrencyCode { get; set; } = string.Empty;

    /// <summary>
    /// The date for which this rate is valid (CNB publication date).
    /// CNB publishes rates on working days; weekends/holidays use the previous rate.
    /// </summary>
    public DateOnly ValidFrom { get; set; }

    /// <summary>
    /// CNB rate: how many CZK you get for <see cref="Amount"/> units of the currency.
    /// Example: EUR rate = 25.255 for Amount = 1 → 1 EUR = 25.255 CZK.
    /// Example: JPY rate = 15.123 for Amount = 100 → 1 JPY = 0.15123 CZK.
    /// </summary>
    public decimal Rate { get; set; }

    /// <summary>
    /// CNB unit count — how many units of the currency the <see cref="Rate"/> is for.
    /// Usually 1 for major currencies, but can be 100 (e.g., JPY, HUF).
    /// </summary>
    public int Amount { get; set; } = 1;

    /// <summary>
    /// Source identifier (e.g., "CNB") for auditing and future extensibility.
    /// </summary>
    public string Source { get; set; } = "CNB";
}
