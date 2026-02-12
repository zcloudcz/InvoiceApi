using InvoiceApi.Application.Dto.VatRate;
using InvoiceApi.Application.Service;
using InvoiceApi.Domain.Entities;
using InvoiceApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZMapper;

namespace InvoiceApi.Infrastructure.Service;

/// <summary>
/// Implementation of VAT rate service.
/// Handles all VAT rate-related business logic.
///
/// Dual-context: uses TenantDbContext when a tenant is available (regular users
/// or impersonating SysAdmin), and falls back to MasterDbContext when SysAdmin
/// operates without impersonation (managing global/master code tables).
/// </summary>
public class VatRateService : IVatRateService
{
    private readonly TenantDbContext _tenantContext;
    private readonly MasterDbContext _masterContext;
    private readonly ITenantResolver _tenantResolver;
    private readonly ILogger<VatRateService> _logger;

    public VatRateService(
        TenantDbContext tenantContext,
        MasterDbContext masterContext,
        ITenantResolver tenantResolver,
        ILogger<VatRateService> logger)
    {
        _tenantContext = tenantContext;
        _masterContext = masterContext;
        _tenantResolver = tenantResolver;
        _logger = logger;
    }

    /// <summary>
    /// Whether we're operating in master context (SysAdmin without impersonation).
    /// </summary>
    private bool IsMasterContext => !_tenantResolver.GetCurrentCompanyId().HasValue;

    /// <summary>
    /// Resolves the correct VatRate DbSet based on context.
    /// </summary>
    private DbSet<VatRate> VatRateSet => IsMasterContext ? _masterContext.VatRate : _tenantContext.VatRate;

    /// <summary>
    /// Resolves the correct DbContext for SaveChanges operations.
    /// </summary>
    private DbContext ActiveContext => IsMasterContext ? _masterContext : _tenantContext;

    /// <summary>
    /// Gets all VAT rates
    /// </summary>
    public async Task<List<VatRateDto>> GetAllVatRatesAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching all VAT rates (includeInactive: {IncludeInactive})", includeInactive);

        // AsNoTracking: read-only list — results are mapped to DTOs
        var query = VatRateSet.AsNoTracking().AsQueryable();

        if (!includeInactive)
        {
            query = query.Where(v => v.IsActive);
        }

        var rates = await query
            .OrderBy(v => v.IsReduced)
            .ThenBy(v => v.Rate)
            .ToListAsync(cancellationToken);

        return rates.Select(MapToDto).ToList();
    }

    /// <summary>
    /// Gets VAT rate by ID
    /// </summary>
    public async Task<VatRateDto?> GetVatRateByIdAsync(
        long vatRateId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching VAT rate by ID: {VatRateId}", vatRateId);

        // AsNoTracking: read-only lookup — result is mapped to DTO
        var rate = await VatRateSet
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == vatRateId, cancellationToken);

        return rate != null ? MapToDto(rate) : null;
    }

    /// <summary>
    /// Gets active VAT rates valid at a specific date
    /// </summary>
    public async Task<List<VatRateDto>> GetActiveVatRatesForDateAsync(
        DateTime? date = null,
        CancellationToken cancellationToken = default)
    {
        var checkDate = date ?? DateTime.UtcNow;
        _logger.LogInformation("Fetching active VAT rates for date: {Date}", checkDate);

        // AsNoTracking: read-only list — results are mapped to DTOs
        var rates = await VatRateSet
            .AsNoTracking()
            .Where(v => v.IsActive &&
                       v.ValidFrom <= checkDate &&
                       (v.ValidTo == null || v.ValidTo >= checkDate))
            .OrderBy(v => v.IsReduced)
            .ThenBy(v => v.Rate)
            .ToListAsync(cancellationToken);

        return rates.Select(MapToDto).ToList();
    }

    /// <summary>
    /// Gets the default standard VAT rate
    /// </summary>
    public async Task<VatRateDto?> GetDefaultStandardRateAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching default standard VAT rate");

        // AsNoTracking: read-only lookup — result is mapped to DTO
        // OrderBy(Id): deterministic ordering — avoids EF warning when predicate could match multiple rows.
        var rate = await VatRateSet
            .AsNoTracking()
            .OrderBy(v => v.Id)
            .FirstOrDefaultAsync(v => v.IsDefault && !v.IsReduced && v.IsActive, cancellationToken);

        return rate != null ? MapToDto(rate) : null;
    }

    /// <summary>
    /// Gets the default reduced VAT rate
    /// </summary>
    public async Task<VatRateDto?> GetDefaultReducedRateAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching default reduced VAT rate");

        // AsNoTracking: read-only lookup — result is mapped to DTO
        // OrderBy(Id): deterministic ordering — avoids EF warning when predicate could match multiple rows.
        var rate = await VatRateSet
            .AsNoTracking()
            .OrderBy(v => v.Id)
            .FirstOrDefaultAsync(v => v.IsDefault && v.IsReduced && v.IsActive, cancellationToken);

        return rate != null ? MapToDto(rate) : null;
    }

    /// <summary>
    /// Creates a new VAT rate
    /// Validates business rules for default rates
    /// </summary>
    public async Task<VatRateDto> CreateVatRateAsync(
        CreateVatRateDto createDto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating new VAT rate: {Name}", createDto.Name);

        // Validate ValidTo is after ValidFrom
        if (createDto.ValidTo.HasValue && createDto.ValidTo.Value < createDto.ValidFrom)
        {
            throw new InvalidOperationException("ValidTo date must be after ValidFrom date");
        }

        // Validate default rate constraint
        if (createDto.IsDefault)
        {
            await ValidateDefaultRateConstraintAsync(null, createDto.IsReduced, cancellationToken);
        }

        var vatRate = new VatRate
        {
            Name = createDto.Name,
            Rate = createDto.Rate,
            ValidFrom = createDto.ValidFrom,
            ValidTo = createDto.ValidTo,
            IsReduced = createDto.IsReduced,
            IsDefault = createDto.IsDefault,
            IsActive = createDto.IsActive
        };

        VatRateSet.Add(vatRate);
        await ActiveContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("VAT rate created with ID: {VatRateId}", vatRate.Id);

        return MapToDto(vatRate);
    }

    /// <summary>
    /// Updates an existing VAT rate
    /// Validates business rules for default rates
    /// </summary>
    public async Task<VatRateDto?> UpdateVatRateAsync(
        long vatRateId,
        UpdateVatRateDto updateDto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Updating VAT rate: {VatRateId}", vatRateId);

        var vatRate = await VatRateSet
            .FirstOrDefaultAsync(v => v.Id == vatRateId, cancellationToken);

        if (vatRate == null)
        {
            _logger.LogWarning("VAT rate not found: {VatRateId}", vatRateId);
            return null;
        }

        // Validate ValidTo is after ValidFrom
        if (updateDto.ValidTo.HasValue && updateDto.ValidTo.Value < updateDto.ValidFrom)
        {
            throw new InvalidOperationException("ValidTo date must be after ValidFrom date");
        }

        // Validate default rate constraint if changing to default
        if (updateDto.IsDefault && (!vatRate.IsDefault || vatRate.IsReduced != updateDto.IsReduced))
        {
            await ValidateDefaultRateConstraintAsync(vatRateId, updateDto.IsReduced, cancellationToken);
        }

        // Update properties
        vatRate.Name = updateDto.Name;
        vatRate.Rate = updateDto.Rate;
        vatRate.ValidFrom = updateDto.ValidFrom;
        vatRate.ValidTo = updateDto.ValidTo;
        vatRate.IsReduced = updateDto.IsReduced;
        vatRate.IsDefault = updateDto.IsDefault;
        vatRate.IsActive = updateDto.IsActive;

        await ActiveContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("VAT rate updated: {VatRateId}", vatRateId);

        return MapToDto(vatRate);
    }

    /// <summary>
    /// Deletes a VAT rate (soft delete)
    /// </summary>
    public async Task<bool> DeleteVatRateAsync(
        long vatRateId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Deleting VAT rate: {VatRateId}", vatRateId);

        var vatRate = await VatRateSet
            .FirstOrDefaultAsync(v => v.Id == vatRateId, cancellationToken);

        if (vatRate == null)
        {
            _logger.LogWarning("VAT rate not found: {VatRateId}", vatRateId);
            return false;
        }

        // Check if rate is used in any invoice items (tenant-only check).
        // In master context (SysAdmin without impersonation), invoices don't exist —
        // skip the FK check and allow soft delete of the master code table record.
        if (!IsMasterContext)
        {
            var isUsedInInvoices = await _tenantContext.InvoiceItem
                .AnyAsync(i => i.VatRateId == vatRateId, cancellationToken);

            if (isUsedInInvoices)
            {
                _logger.LogWarning("Cannot delete VAT rate {VatRateId} - it is used in invoice items", vatRateId);
                throw new InvalidOperationException("Cannot delete VAT rate that is used in invoices. Set it as inactive instead.");
            }
        }

        // Soft delete
        vatRate.IsActive = false;
        await ActiveContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("VAT rate deleted (soft): {VatRateId}", vatRateId);

        return true;
    }

    /// <summary>
    /// Sets a VAT rate as default
    /// Automatically unsets the previous default rate of the same type
    /// </summary>
    public async Task<VatRateDto?> SetAsDefaultAsync(
        long vatRateId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Setting VAT rate as default: {VatRateId}", vatRateId);

        var vatRate = await VatRateSet
            .FirstOrDefaultAsync(v => v.Id == vatRateId, cancellationToken);

        if (vatRate == null)
        {
            _logger.LogWarning("VAT rate not found: {VatRateId}", vatRateId);
            return null;
        }

        // If already default, nothing to do
        if (vatRate.IsDefault)
        {
            _logger.LogInformation("VAT rate {VatRateId} is already default", vatRateId);
            return MapToDto(vatRate);
        }

        // Unset the previous default rate of the same type (standard/reduced).
        // OrderBy(Id): deterministic ordering — avoids EF warning when predicate could match multiple rows.
        var previousDefault = await VatRateSet
            .OrderBy(v => v.Id)
            .FirstOrDefaultAsync(v => v.IsDefault && v.IsReduced == vatRate.IsReduced && v.Id != vatRateId, cancellationToken);

        if (previousDefault != null)
        {
            _logger.LogInformation("Unsetting previous default rate: {PreviousDefaultId}", previousDefault.Id);
            previousDefault.IsDefault = false;
        }

        // Set new default
        vatRate.IsDefault = true;
        await ActiveContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("VAT rate set as default: {VatRateId}", vatRateId);

        return MapToDto(vatRate);
    }

    /// <summary>
    /// Maps a VatRate entity to VatRateDto, manually setting inherited BaseEntity
    /// properties (Id, CreatedAt, UpdatedAt) that ZMapper cannot see.
    /// </summary>
    /// <summary>
    /// Maps a VatRate entity to VatRateDto using ZMapper v1.1.0.
    /// ZMapper now handles all properties including inherited BaseEntity (Id, CreatedAt, UpdatedAt).
    /// </summary>
    private static VatRateDto MapToDto(VatRate entity)
    {
        return entity.ToVatRateDto();
    }

    /// <summary>
    /// Validates that only one default rate exists for each type (standard/reduced)
    /// </summary>
    /// <param name="excludeVatRateId">VAT rate ID to exclude from check (when updating)</param>
    /// <param name="isReduced">Is this a reduced rate?</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task ValidateDefaultRateConstraintAsync(
        long? excludeVatRateId,
        bool isReduced,
        CancellationToken cancellationToken)
    {
        var query = VatRateSet
            .Where(v => v.IsDefault && v.IsReduced == isReduced);

        if (excludeVatRateId.HasValue)
        {
            query = query.Where(v => v.Id != excludeVatRateId.Value);
        }

        var existingDefaultCount = await query.CountAsync(cancellationToken);

        if (existingDefaultCount > 0)
        {
            var rateType = isReduced ? "reduced" : "standard";
            throw new InvalidOperationException(
                $"A default {rateType} VAT rate already exists. " +
                $"Please unset the existing default rate before setting a new one, or use SetAsDefault method.");
        }
    }

}
