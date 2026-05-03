using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.ReverseChargeCode;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZMapper;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Read-only service for reverse charge codes (kódy předmětu plnění PDP).
///
/// All queries run on the TenantDbContext — each tenant owns its own copy of the code table,
/// seeded from the MFČR číselník at provisioning time (same pattern as VatRate and Currency).
///
/// AsNoTracking is used on all reads because results are immediately projected to DTOs
/// and no entity change-tracking is needed.
/// </summary>
public class ReverseChargeCodeService : IReverseChargeCodeService
{
    private readonly TenantDbContext _tenantContext;
    private readonly ILogger<ReverseChargeCodeService> _logger;

    public ReverseChargeCodeService(
        TenantDbContext tenantContext,
        ILogger<ReverseChargeCodeService> logger)
    {
        _tenantContext = tenantContext;
        _logger = logger;
    }

    /// <summary>
    /// Returns all active codes whose validity window includes today.
    /// A code is active when IsActive == true AND (ValidTo == null OR ValidTo >= today).
    /// Results are ordered by Code ascending.
    /// </summary>
    public async Task<List<ReverseChargeCodeDto>> GetAllActiveAsync(
        CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _logger.LogInformation("Fetching active reverse charge codes for date: {Today}", today);

        // AsNoTracking: read-only list — entities are projected to DTOs immediately.
        var codes = await _tenantContext.ReverseChargeCode
            .AsNoTracking()
            .Where(c => c.IsActive
                     && c.ValidFrom <= today
                     && (c.ValidTo == null || c.ValidTo >= today))
            .OrderBy(c => c.Code)
            .ToListAsync(cancellationToken);

        _logger.LogInformation("Found {Count} active reverse charge codes", codes.Count);

        return codes.Select(MapToDto).ToList();
    }

    /// <summary>
    /// Looks up a code by its MFČR code string (e.g., "5", "1a").
    /// Returns null when no matching row exists.
    /// </summary>
    public async Task<ReverseChargeCodeDto?> GetByCodeAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching reverse charge code by code: {Code}", code);

        // AsNoTracking: read-only lookup.
        var entity = await _tenantContext.ReverseChargeCode
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Code == code, cancellationToken);

        if (entity == null)
        {
            _logger.LogWarning("Reverse charge code not found: {Code}", code);
        }

        return entity != null ? MapToDto(entity) : null;
    }

    /// <summary>
    /// Looks up a code by its database primary key.
    /// Returns null when no record with the given Id exists.
    /// </summary>
    public async Task<ReverseChargeCodeDto?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching reverse charge code by ID: {Id}", id);

        // AsNoTracking: read-only lookup.
        var entity = await _tenantContext.ReverseChargeCode
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (entity == null)
        {
            _logger.LogWarning("Reverse charge code not found, Id: {Id}", id);
        }

        return entity != null ? MapToDto(entity) : null;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a ReverseChargeCode entity to its DTO using ZMapper source-generated extension.
    /// ZMapper v1.1.0 handles all properties, including inherited BaseEntity fields.
    /// </summary>
    private static ReverseChargeCodeDto MapToDto(ReverseChargeCode entity) =>
        entity.ToReverseChargeCodeDto();
}
