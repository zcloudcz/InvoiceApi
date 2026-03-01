namespace Fakvio.Contracts.Dto.VatRate;

/// <summary>
/// DTO for VAT rate data
/// Used for API responses when returning VAT rate information
/// </summary>
public class VatRateDto
{
    /// <summary>
    /// VAT rate ID
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Name/description of this VAT rate
    /// Example: "DPH 21% standardní"
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The tax rate as a percentage
    /// Example: 21.00 for 21%
    /// </summary>
    public decimal Rate { get; set; }

    /// <summary>
    /// When does this rate become valid
    /// </summary>
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
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Is this rate currently active?
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// When was this record created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When was this record last updated
    /// </summary>
    public DateTime? UpdatedAt { get; set; }
}
