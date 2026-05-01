using Fakvio.Application.Common.Extensions;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Currency;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZMapper;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Implementation of currency service.
/// Handles all business logic for currency management and CZK conversion.
///
/// Currency master data (CZK, EUR, USD, ...) uses MasterDbContext — currencies are
/// global/shared data. Exchange rate records, however, are per-tenant (stored in the
/// tenant schema) so that each tenant can have an independent rate history.
/// </summary>
public class CurrencyService : ICurrencyService
{
    private readonly MasterDbContext _masterContext;
    private readonly TenantDbContext _tenantContext;
    private readonly ITenantResolver _tenantResolver;
    private readonly IExchangeRateProvider _cnbProvider;
    private readonly ILogger<CurrencyService> _logger;

    public CurrencyService(
        TenantDbContext tenantContext,
        MasterDbContext masterContext,
        ITenantResolver tenantResolver,
        IExchangeRateProvider cnbProvider,
        ILogger<CurrencyService> logger)
    {
        _masterContext = masterContext;
        _tenantContext = tenantContext;
        _tenantResolver = tenantResolver;
        _cnbProvider = cnbProvider;
        _logger = logger;
    }

    public async Task<List<CurrencyDto>> GetActiveCurrenciesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching all active currencies");

        // AsNoTracking: read-only list — results are mapped to DTOs
        var currencies = await _masterContext.Currency
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.SortOrder)
            .ToListAsync(cancellationToken);

        return currencies.Select(c => MapToDto(c)).ToList();
    }

    public async Task<PagedResult<CurrencyDto>> GetCurrenciesPagedAsync(
        int page = 1,
        int pageSize = 10,
        string? search = null,
        bool? isActive = null,
        string sortBy = "SortOrder",
        bool isDescending = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching paged currencies (Page: {Page}, PageSize: {PageSize}, Search: {Search})",
            page, pageSize, search);

        // AsNoTracking: read-only paged query — results are mapped to DTOs
        var query = _masterContext.Currency.AsNoTracking().AsQueryable();

        // Apply filters
        if (isActive.HasValue)
            query = query.Where(c => c.IsActive == isActive.Value);

        // Search filter - search across Code, Name, Symbol
        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.ToLower();
            query = query.Where(c =>
                c.Code.ToLower().Contains(searchLower) ||
                c.Name.ToLower().Contains(searchLower) ||
                c.Symbol.Contains(searchLower));
        }

        // Apply sorting
        var validSortFields = new[] { "Code", "Name", "Symbol", "SortOrder", "IsActive", "CreatedAt" };
        var sortField = !string.IsNullOrWhiteSpace(sortBy) && validSortFields.Contains(sortBy, StringComparer.OrdinalIgnoreCase)
            ? sortBy : "SortOrder";

        query = query.ApplySorting(sortField, isDescending);

        // Get paged results
        var pagedResult = await query.ToPagedResultAsync(page, pageSize, cancellationToken);

        // Map to DTOs
        return new PagedResult<CurrencyDto>(
            pagedResult.Items.Select(c => MapToDto(c)).ToList(),
            pagedResult.TotalCount,
            pagedResult.PageNumber,
            pagedResult.PageSize);
    }

    public async Task<CurrencyDto?> GetCurrencyByIdAsync(long currencyId, CancellationToken cancellationToken = default)
    {
        // AsNoTracking: read-only lookup — result is mapped to DTO
        var currency = await _masterContext.Currency
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == currencyId, cancellationToken);

        return currency == null ? null : MapToDto(currency);
    }

    public async Task<CurrencyDto?> GetCurrencyByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        // AsNoTracking: read-only lookup — result is mapped to DTO
        var currency = await _masterContext.Currency
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Code.ToLower() == code.ToLower(), cancellationToken);

        return currency == null ? null : MapToDto(currency);
    }

    public async Task<CurrencyDto> CreateCurrencyAsync(CreateCurrencyDto createDto, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating new currency: {Code}", createDto.Code);

        // Check if currency code already exists
        var existingCurrency = await _masterContext.Currency
            .FirstOrDefaultAsync(c => c.Code.ToLower() == createDto.Code.ToLower(), cancellationToken);

        if (existingCurrency != null)
            throw new InvalidOperationException($"Currency with code '{createDto.Code}' already exists");

        var currency = new Currency
        {
            Code = createDto.Code.ToUpper(),
            Name = createDto.Name,
            Symbol = createDto.Symbol,
            DecimalPlaces = createDto.DecimalPlaces,
            IsActive = true,
            SortOrder = createDto.SortOrder,
            DisplayFormat = createDto.DisplayFormat
        };

        _masterContext.Currency.Add(currency);
        await _masterContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created currency {Code} with ID {Id}", currency.Code, currency.Id);

        return MapToDto(currency);
    }

    public async Task<CurrencyDto?> UpdateCurrencyAsync(long currencyId, UpdateCurrencyDto updateDto, CancellationToken cancellationToken = default)
    {
        var currency = await _masterContext.Currency
            .FirstOrDefaultAsync(c => c.Id == currencyId, cancellationToken);

        if (currency == null)
            return null;

        _logger.LogInformation("Updating currency {Code} (ID: {Id})", currency.Code, currency.Id);

        // Update fields
        if (updateDto.Name != null)
            currency.Name = updateDto.Name;

        if (updateDto.Symbol != null)
            currency.Symbol = updateDto.Symbol;

        if (updateDto.DecimalPlaces.HasValue)
            currency.DecimalPlaces = updateDto.DecimalPlaces.Value;

        if (updateDto.IsActive.HasValue)
            currency.IsActive = updateDto.IsActive.Value;

        if (updateDto.SortOrder.HasValue)
            currency.SortOrder = updateDto.SortOrder.Value;

        if (updateDto.DisplayFormat != null)
            currency.DisplayFormat = updateDto.DisplayFormat;

        await _masterContext.SaveChangesAsync(cancellationToken);

        return MapToDto(currency);
    }

    /// <summary>
    /// Soft-deletes a currency by setting IsActive = false.
    /// This is safe even when the currency is referenced by invoices or clients,
    /// because the record remains in the database — only hidden from active lists.
    /// </summary>
    public async Task<bool> DeleteCurrencyAsync(long currencyId, CancellationToken cancellationToken = default)
    {
        var currency = await _masterContext.Currency
            .FirstOrDefaultAsync(c => c.Id == currencyId, cancellationToken);

        if (currency == null)
            return false;

        _logger.LogInformation("Soft-deleting currency {Code} (ID: {Id})", currency.Code, currency.Id);

        // Soft delete — deactivate instead of removing from database
        currency.IsActive = false;
        await _masterContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Soft-deleted currency {Code} (ID: {Id})", currency.Code, currency.Id);

        return true;
    }

    /// <inheritdoc />
    public async Task<decimal> ConvertToCzkAsync(
        decimal amount,
        string currencyCode,
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        // CZK is always 1:1 — no conversion needed
        if (string.Equals(currencyCode, "CZK", StringComparison.OrdinalIgnoreCase))
            return amount;

        var rate = await FindRateOrThrowAsync(currencyCode, date, cancellationToken);

        // CNB formula: amount_in_czk = amount * (Rate / Amount)
        // Amount is the CNB unit count (e.g., 100 for JPY means Rate covers 100 units)
        var result = amount * (rate.Rate / rate.Amount);

        return Math.Round(result, 2, MidpointRounding.AwayFromZero);
    }

    /// <inheritdoc />
    public async Task<ExchangeRateDto?> GetExchangeRateAsync(
        string currencyCode,
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        // CZK has no stored rate — it is always 1:1
        if (string.Equals(currencyCode, "CZK", StringComparison.OrdinalIgnoreCase))
            return null;

        var rate = await _tenantContext.ExchangeRate
            .AsNoTracking()
            .Where(r => r.CurrencyCode == currencyCode.ToUpperInvariant() && r.ValidFrom <= date)
            .OrderByDescending(r => r.ValidFrom)
            .FirstOrDefaultAsync(cancellationToken);

        return rate == null ? null : MapExchangeRateToDto(rate);
    }

    /// <inheritdoc />
    public async Task<int> RefreshExchangeRatesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("CurrencyService: manual exchange rate refresh triggered");

        // Fetch today's rates from CNB
        var rates = await _cnbProvider.FetchRatesAsync(null, cancellationToken);

        if (rates.Count == 0)
        {
            _logger.LogWarning("CurrencyService: CNB returned 0 rates during manual refresh");
            return 0;
        }

        // Determine which (code, date) pairs are already stored — skip existing
        var today = rates.Select(r => r.ValidFrom).Distinct().ToList();
        var existing = await _tenantContext.ExchangeRate
            .Where(r => today.Contains(r.ValidFrom))
            .Select(r => new { r.CurrencyCode, r.ValidFrom })
            .ToListAsync(cancellationToken);

        var existingSet = existing
            .Select(e => (e.CurrencyCode, e.ValidFrom))
            .ToHashSet();

        var newRates = rates
            .Where(r => !existingSet.Contains((r.CurrencyCode, r.ValidFrom)))
            .ToList();

        if (newRates.Count == 0)
        {
            _logger.LogInformation("CurrencyService: all rates for today already present — nothing to import");
            return 0;
        }

        _tenantContext.ExchangeRate.AddRange(newRates);
        await _tenantContext.SaveChangesAsync(cancellationToken);

        // Also update LastRunAt in master DB for this company (manual refresh counts as a run)
        var companyId = _tenantResolver.GetCurrentCompanyId();
        if (companyId.HasValue)
        {
            var settings = await _masterContext.CompanySystemSettings
                .FirstOrDefaultAsync(s => s.CompanyId == companyId.Value, cancellationToken);
            if (settings != null)
            {
                settings.ExchangeRateLastRunAt = DateTime.UtcNow;
                await _masterContext.SaveChangesAsync(cancellationToken);
            }
        }

        _logger.LogInformation("CurrencyService: manual refresh saved {Count} new rate records", newRates.Count);
        return newRates.Count;
    }

    /// <inheritdoc />
    public async Task<ExchangeRateSettingsDto> GetExchangeRateSettingsAsync(CancellationToken cancellationToken = default)
    {
        var companyId = _tenantResolver.GetCurrentCompanyId()
            ?? throw new InvalidOperationException("No tenant context — cannot read exchange rate settings.");

        var settings = await _masterContext.CompanySystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, cancellationToken)
            ?? throw new InvalidOperationException($"CompanySystemSettings not found for CompanyId {companyId}.");

        return new ExchangeRateSettingsDto
        {
            UpdateMode = settings.ExchangeRateUpdateMode,
            UpdateDayOfWeek = settings.ExchangeRateUpdateDayOfWeek,
            LastRunAt = settings.ExchangeRateLastRunAt
        };
    }

    /// <inheritdoc />
    public async Task<ExchangeRateSettingsDto> UpdateExchangeRateSettingsAsync(
        UpdateExchangeRateSettingsDto dto,
        CancellationToken cancellationToken = default)
    {
        var companyId = _tenantResolver.GetCurrentCompanyId()
            ?? throw new InvalidOperationException("No tenant context — cannot update exchange rate settings.");

        var settings = await _masterContext.CompanySystemSettings
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, cancellationToken)
            ?? throw new InvalidOperationException($"CompanySystemSettings not found for CompanyId {companyId}.");

        settings.ExchangeRateUpdateMode = dto.UpdateMode;
        settings.ExchangeRateUpdateDayOfWeek = dto.UpdateDayOfWeek;

        await _masterContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "CurrencyService: exchange rate update mode changed to {Mode} for CompanyId {CompanyId}",
            dto.UpdateMode, companyId);

        return new ExchangeRateSettingsDto
        {
            UpdateMode = settings.ExchangeRateUpdateMode,
            UpdateDayOfWeek = settings.ExchangeRateUpdateDayOfWeek,
            LastRunAt = settings.ExchangeRateLastRunAt
        };
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Finds the most recent exchange rate for <paramref name="currencyCode"/> valid on
    /// or before <paramref name="date"/>. Throws <see cref="ExchangeRateNotFoundException"/>
    /// if no rate is found.
    /// </summary>
    private async Task<ExchangeRate> FindRateOrThrowAsync(
        string currencyCode,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var normalizedCode = currencyCode.ToUpperInvariant();

        // Most recent rate with ValidFrom <= target date — handles weekends / holidays.
        var rate = await _tenantContext.ExchangeRate
            .AsNoTracking()
            .Where(r => r.CurrencyCode == normalizedCode && r.ValidFrom <= date)
            .OrderByDescending(r => r.ValidFrom)
            .FirstOrDefaultAsync(cancellationToken);

        if (rate == null)
        {
            _logger.LogWarning(
                "No exchange rate found for currency {Code} on or before {Date}",
                currencyCode, date);
            throw new ExchangeRateNotFoundException(currencyCode, date);
        }

        return rate;
    }

    /// <summary>
    /// Maps a Currency entity to CurrencyDto using ZMapper v1.2.0.
    /// </summary>
    private static CurrencyDto MapToDto(Currency entity)
    {
        return entity.ToCurrencyDto();
    }

    /// <summary>
    /// Maps an ExchangeRate entity to ExchangeRateDto manually
    /// (no ZMapper profile needed — straightforward flat mapping).
    /// </summary>
    private static ExchangeRateDto MapExchangeRateToDto(ExchangeRate entity)
    {
        return new ExchangeRateDto
        {
            Id = entity.Id,
            CurrencyCode = entity.CurrencyCode,
            ValidFrom = entity.ValidFrom,
            Rate = entity.Rate,
            Amount = entity.Amount,
            Source = entity.Source
        };
    }
}
