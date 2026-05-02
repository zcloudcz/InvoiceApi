using Fakvio.Application.Common.Extensions;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Currency;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using ZMapper;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Implementation of currency service.
/// Handles all business logic for currency management.
///
/// All operations use MasterDbContext — currencies are global/shared data (CZK, EUR, USD).
/// There is no need to duplicate currencies per tenant. Tenant DB has a Currency table
/// only for FK integrity (Invoice → Currency), populated during provisioning.
/// </summary>
public class CurrencyService : ICurrencyService
{
    private readonly MasterDbContext _masterContext;
    private readonly ILogger<CurrencyService> _logger;

    public CurrencyService(
        TenantDbContext tenantContext,
        MasterDbContext masterContext,
        ITenantResolver tenantResolver,
        ILogger<CurrencyService> logger)
    {
        _masterContext = masterContext;
        // tenantContext and tenantResolver kept in constructor signature for DI compatibility
        // but no longer used — currencies are global, all reads/writes go through master context.
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
    /// Maps a Currency entity to CurrencyDto, manually setting inherited BaseEntity
    /// properties (Id, CreatedAt, UpdatedAt) that ZMapper cannot see.
    /// </summary>
    /// <summary>
    /// Maps a Currency entity to CurrencyDto using ZMapper v1.1.0.
    /// ZMapper now handles all properties including inherited BaseEntity (Id, CreatedAt, UpdatedAt).
    /// </summary>
    private static CurrencyDto MapToDto(Currency entity)
    {
        return entity.ToCurrencyDto();
    }

    /// <summary>
    /// Converts an amount in <paramref name="currencyCode"/> to CZK.
    ///
    /// Current implementation: returns <paramref name="amount"/> unchanged for CZK;
    /// for foreign currencies it logs a warning and returns the amount as-is until
    /// issue #36 (ČNB exchange-rate integration) provides a persisted rate table.
    ///
    /// Once #36 is merged, this method should look up the ČNB rate for <paramref name="date"/>
    /// from the ExchangeRate table and multiply accordingly.
    /// </summary>
    public Task<decimal> ConvertToCzkAsync(
        decimal amount,
        string currencyCode,
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        // CZK → no conversion needed.
        if (string.Equals(currencyCode, "CZK", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(amount);

        // Non-CZK: #36 (ČNB rate table) is not yet merged.
        // Return the amount unchanged and warn so the gap is visible in logs.
        _logger.LogWarning(
            "ConvertToCzkAsync: no ČNB rate table available yet (issue #36). " +
            "Returning {Amount} {Currency} as-is. " +
            "EPO amounts for non-CZK invoices will be incorrect until #36 is integrated.",
            amount, currencyCode);

        return Task.FromResult(amount);
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

}
