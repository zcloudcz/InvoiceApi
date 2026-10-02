using System.ComponentModel.DataAnnotations;
using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.OssVatRate;

/// <summary>Create DTO for a new EU OSS VAT rate row. SysAdmin only.</summary>
public class CreateOssVatRateDto
{
    /// <summary>ISO 3166-1 alpha-2 country code (e.g. "DE"). Validated against the EU member list server-side.</summary>
    [Required]
    [StringLength(2, MinimumLength = 2)]
    public string CountryCode { get; set; } = string.Empty;

    [Range(0, 100)]
    public decimal Rate { get; set; }

    public EOssVatRateCategory Category { get; set; }

    [StringLength(200)]
    public string? Description { get; set; }

    public DateOnly ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public bool IsActive { get; set; } = true;
}
