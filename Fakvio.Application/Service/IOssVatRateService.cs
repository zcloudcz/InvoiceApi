using Fakvio.Contracts.Dto.OssVatRate;

namespace Fakvio.Application.Service;

/// <summary>
/// Reads/maintains the EU OSS (One-Stop-Shop) VAT rate code table (Master DB, no tenant
/// copy — see DEVGUIDE §4.16 / §11.2). Any authenticated tenant user can read (the invoice
/// item editor needs it); only SysAdmin can write (it is statutory data, not per-tenant).
/// </summary>
public interface IOssVatRateService
{
    /// <summary>All rates, optionally including inactive ones. Ordered by country then rate.</summary>
    Task<List<OssVatRateDto>> GetAllAsync(bool includeInactive = false, CancellationToken ct = default);

    /// <summary>
    /// Active rates for a single EU country valid on <paramref name="date"/> (default: today).
    /// This is what the invoice item editor offers for an OSS invoice's destination country,
    /// and what server-side validation checks the chosen rate against.
    /// </summary>
    Task<List<OssVatRateDto>> GetForCountryAsync(string countryCode, DateOnly? date = null, CancellationToken ct = default);

    Task<OssVatRateDto?> GetByIdAsync(long id, CancellationToken ct = default);

    Task<OssVatRateDto> CreateAsync(CreateOssVatRateDto dto, CancellationToken ct = default);

    Task<OssVatRateDto?> UpdateAsync(long id, UpdateOssVatRateDto dto, CancellationToken ct = default);

    /// <summary>Soft delete (IsActive = false) — historical invoices keep referencing the rate value.</summary>
    Task<bool> DeactivateAsync(long id, CancellationToken ct = default);
}
