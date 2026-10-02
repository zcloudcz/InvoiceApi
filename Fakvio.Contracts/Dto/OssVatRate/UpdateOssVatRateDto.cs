using System.ComponentModel.DataAnnotations;
using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.OssVatRate;

/// <summary>Update DTO for an existing EU OSS VAT rate row. SysAdmin only.</summary>
public class UpdateOssVatRateDto
{
    [Range(0, 100)]
    public decimal Rate { get; set; }

    public EOssVatRateCategory Category { get; set; }

    [StringLength(200)]
    public string? Description { get; set; }

    public DateOnly ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public bool IsActive { get; set; }
}
