using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.OssVatRate;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service.Oss;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZMapper;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Implementation of <see cref="IOssVatRateService"/>. Master DB only — see
/// Domain.Entities.OssVatRate for why this code table has no tenant copy.
/// </summary>
public class OssVatRateService : IOssVatRateService
{
    private readonly MasterDbContext _context;
    private readonly ILogger<OssVatRateService> _logger;

    public OssVatRateService(MasterDbContext context, ILogger<OssVatRateService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<List<OssVatRateDto>> GetAllAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        var query = _context.OssVatRate.AsNoTracking().AsQueryable();
        if (!includeInactive)
            query = query.Where(r => r.IsActive);

        var rates = await query
            .OrderBy(r => r.CountryCode)
            .ThenBy(r => r.Rate)
            .ToListAsync(ct);

        return rates.Select(r => r.ToOssVatRateDto()).ToList();
    }

    public async Task<List<OssVatRateDto>> GetForCountryAsync(string countryCode, DateOnly? date = null, CancellationToken ct = default)
    {
        var checkDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var normalized = countryCode.Trim().ToUpperInvariant();

        var rates = await _context.OssVatRate
            .AsNoTracking()
            .Where(r => r.CountryCode == normalized
                && r.IsActive
                && r.ValidFrom <= checkDate
                && (r.ValidTo == null || r.ValidTo >= checkDate))
            .OrderBy(r => r.Rate)
            .ToListAsync(ct);

        return rates.Select(r => r.ToOssVatRateDto()).ToList();
    }

    public async Task<OssVatRateDto?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        var rate = await _context.OssVatRate.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        return rate?.ToOssVatRateDto();
    }

    public async Task<OssVatRateDto> CreateAsync(CreateOssVatRateDto dto, CancellationToken ct = default)
    {
        var countryCode = dto.CountryCode.Trim().ToUpperInvariant();
        if (!EuCountries.IsOtherMemberState(countryCode))
            throw new InvalidOperationException($"'{countryCode}' is not an EU member state other than CZ.");

        var entity = new OssVatRate
        {
            CountryCode = countryCode,
            Rate = dto.Rate,
            Category = dto.Category,
            Description = dto.Description,
            ValidFrom = dto.ValidFrom,
            ValidTo = dto.ValidTo,
            IsActive = dto.IsActive
        };

        _context.OssVatRate.Add(entity);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Created OSS VAT rate {CountryCode} {Rate}% (Id={Id})", entity.CountryCode, entity.Rate, entity.Id);
        return entity.ToOssVatRateDto();
    }

    public async Task<OssVatRateDto?> UpdateAsync(long id, UpdateOssVatRateDto dto, CancellationToken ct = default)
    {
        var entity = await _context.OssVatRate.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (entity == null) return null;

        entity.Rate = dto.Rate;
        entity.Category = dto.Category;
        entity.Description = dto.Description;
        entity.ValidFrom = dto.ValidFrom;
        entity.ValidTo = dto.ValidTo;
        entity.IsActive = dto.IsActive;

        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Updated OSS VAT rate {Id}", id);
        return entity.ToOssVatRateDto();
    }

    public async Task<bool> DeactivateAsync(long id, CancellationToken ct = default)
    {
        var entity = await _context.OssVatRate.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (entity == null) return false;

        entity.IsActive = false;
        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Deactivated OSS VAT rate {Id}", id);
        return true;
    }
}
