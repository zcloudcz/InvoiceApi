using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.OssVatRate;

/// <summary>Read DTO for an EU OSS VAT rate row (see Domain.Entities.OssVatRate).</summary>
public class OssVatRateDto
{
    public long Id { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public decimal Rate { get; set; }
    public EOssVatRateCategory Category { get; set; }
    public string? Description { get; set; }
    public DateOnly ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
