namespace InvoiceApi.Contracts.Dto.Currency;

/// <summary>
/// DTO for currency data
/// Used for API responses
/// </summary>
public class CurrencyDto
{
    public long Id { get; set; }

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
    public int DecimalPlaces { get; set; }

    /// <summary>
    /// Is this currency active/available for use?
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Display order in dropdowns
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Format template for displaying amounts (e.g., "{0:N2} Kč", "€{0:N2}")
    /// </summary>
    public string? DisplayFormat { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
