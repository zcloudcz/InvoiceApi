using System.ComponentModel.DataAnnotations;

namespace InvoiceApi.Contracts.Dto.VatRate;

/// <summary>
/// DTO for updating an existing VAT rate
/// </summary>
public class UpdateVatRateDto
{
    /// <summary>
    /// Name/description of this VAT rate
    /// Example: "DPH 21% standardní"
    /// </summary>
    [Required(ErrorMessage = "Name is required")]
    [StringLength(200, ErrorMessage = "Name cannot exceed 200 characters")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The tax rate as a percentage
    /// Example: 21.00 for 21%
    /// </summary>
    [Required(ErrorMessage = "Rate is required")]
    [Range(0, 100, ErrorMessage = "Rate must be between 0 and 100")]
    public decimal Rate { get; set; }

    /// <summary>
    /// When does this rate become valid
    /// </summary>
    [Required(ErrorMessage = "ValidFrom date is required")]
    public DateTime ValidFrom { get; set; }

    /// <summary>
    /// When does this rate stop being valid
    /// Null = valid indefinitely
    /// </summary>
    public DateTime? ValidTo { get; set; }

    /// <summary>
    /// Is this a reduced VAT rate?
    /// </summary>
    public bool IsReduced { get; set; }

    /// <summary>
    /// Is this the default rate to use?
    /// Business rule: Only one default standard and one default reduced rate allowed
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Is this rate currently active?
    /// </summary>
    public bool IsActive { get; set; }
}
