using System.ComponentModel.DataAnnotations;

namespace Fakvio.Contracts.Dto.Currency;

/// <summary>
/// DTO for updating an existing currency
/// Only SysAdmin can update currencies
/// Currency code cannot be changed once created
/// </summary>
public class UpdateCurrencyDto
{
    /// <summary>
    /// Full currency name (e.g., "Czech Koruna", "Euro")
    /// </summary>
    [StringLength(100)]
    public string? Name { get; set; }

    /// <summary>
    /// Currency symbol (e.g., "Kč", "€", "$")
    /// </summary>
    [StringLength(10)]
    public string? Symbol { get; set; }

    /// <summary>
    /// Number of decimal places (usually 2, but JPY has 0)
    /// </summary>
    [Range(0, 4, ErrorMessage = "Decimal places must be between 0 and 4")]
    public int? DecimalPlaces { get; set; }

    /// <summary>
    /// Is this currency active/available for use?
    /// </summary>
    public bool? IsActive { get; set; }

    /// <summary>
    /// Display order in dropdowns
    /// </summary>
    public int? SortOrder { get; set; }

    /// <summary>
    /// Format template for displaying amounts (e.g., "{0:N2} Kč", "€{0:N2}")
    /// </summary>
    [StringLength(50)]
    public string? DisplayFormat { get; set; }
}
