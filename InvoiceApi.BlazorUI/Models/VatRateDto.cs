namespace InvoiceApi.BlazorUI.Models;

/// <summary>
/// VAT rate data transfer object
/// </summary>
public class VatRateDto
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Rate { get; set; }
    public DateTime ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public bool IsReduced { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// DTO for creating a new VAT rate
/// </summary>
public class CreateVatRateDto
{
    public string Name { get; set; } = string.Empty;
    public decimal Rate { get; set; }
    public DateTime ValidFrom { get; set; } = DateTime.UtcNow;
    public DateTime? ValidTo { get; set; }
    public bool IsReduced { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// DTO for updating an existing VAT rate
/// </summary>
public class UpdateVatRateDto
{
    public string Name { get; set; } = string.Empty;
    public decimal Rate { get; set; }
    public DateTime ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public bool IsReduced { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; }
}
