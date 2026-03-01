using Fakvio.Domain.Common;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Currency master data (maintained by SysAdmin)
/// Represents available currencies for invoicing
/// </summary>
public class Currency : BaseEntity
{
    /// <summary>
    /// ISO 4217 currency code (e.g., "CZK", "EUR", "USD")
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Full currency name (e.g., "Czech Koruna", "Euro")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Currency symbol (e.g., "Kč", "€", "$")
    /// </summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// Number of decimal places (usually 2, but JPY has 0)
    /// </summary>
    public int DecimalPlaces { get; set; } = 2;

    /// <summary>
    /// Is this currency active/available for use?
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Display order in dropdowns
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Format template for displaying amounts (e.g., "{0:N2} Kč", "€{0:N2}")
    /// </summary>
    public string? DisplayFormat { get; set; }
}
