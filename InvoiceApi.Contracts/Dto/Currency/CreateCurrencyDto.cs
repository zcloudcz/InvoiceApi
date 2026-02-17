using System.ComponentModel.DataAnnotations;

namespace InvoiceApi.Contracts.Dto.Currency;

/// <summary>
/// DTO for creating a new currency
/// Only SysAdmin can create currencies
/// </summary>
public class CreateCurrencyDto
{
    /// <summary>
    /// ISO 4217 currency code (e.g., "CZK", "EUR", "USD")
    /// Must be unique and exactly 3 characters
    /// </summary>
    [Required]
    [StringLength(3, MinimumLength = 3, ErrorMessage = "Currency code must be exactly 3 characters")]
    [RegularExpression("^[A-Z]{3}$", ErrorMessage = "Currency code must be 3 uppercase letters")]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Full currency name (e.g., "Czech Koruna", "Euro")
    /// </summary>
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Currency symbol (e.g., "Kč", "€", "$")
    /// </summary>
    [Required]
    [StringLength(10, MinimumLength = 1)]
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// Number of decimal places (usually 2, but JPY has 0)
    /// </summary>
    [Range(0, 4, ErrorMessage = "Decimal places must be between 0 and 4")]
    public int DecimalPlaces { get; set; } = 2;

    /// <summary>
    /// Display order in dropdowns
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Format template for displaying amounts (e.g., "{0:N2} Kč", "€{0:N2}")
    /// </summary>
    [StringLength(50)]
    public string? DisplayFormat { get; set; }
}
