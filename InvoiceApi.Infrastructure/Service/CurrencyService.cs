using InvoiceApi.Application.Common.Extensions;
using InvoiceApi.Contracts.Common.Pagination;
using InvoiceApi.Contracts.Dto.Currency;
using InvoiceApi.Application.Service;
using InvoiceApi.Domain.Entities;
using InvoiceApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using ZMapper;

namespace InvoiceApi.Infrastructure.Service;

/// <summary>
/// Implementation of currency service.
/// Handles all business logic for currency management.
///
/// Dual-context: uses TenantDbContext when a tenant is available (regular users
/// or impersonating SysAdmin), and falls back to MasterDbContext when SysAdmin
/// operates without impersonation (managing global/master code tables).
/// </summary>
public class CurrencyService : ICurrencyService
{
    private readonly TenantDbContext _tenantContext;
    private readonly MasterDbContext _masterContext;
    private readonly ITenantResolver _tenantResolver;
    private readonly ILogger<CurrencyService> _logger;

    public CurrencyService(
        TenantDbContext tenantContext,
        MasterDbContext masterContext,
        ITenantResolver tenantResolver,
        ILogger<CurrencyService> logger)
    {
        _tenantContext = tenantContext;
        _masterContext = masterContext;
        _tenantResolver = tenantResolver;
        _logger = logger;
    }

    /// <summary>
    /// Whether we're operating in master context (SysAdmin without impersonation).
    /// When true, queries go to MasterDbContext; when false, to TenantDbContext.
    /// </summary>
    private bool IsMasterContext => !_tenantResolver.GetCurrentCompanyId().HasValue;

    /// <summary>
    /// Resolves the correct Currency DbSet based on context.
    /// </summary>
    private DbSet<Currency> CurrencySet => IsMasterContext ? _masterContext.Currency : _tenantContext.Currency;

    /// <summary>
    /// Resolves the correct DbContext for SaveChanges operations.
    /// </summary>
    private DbContext ActiveContext => IsMasterContext ? _masterContext : _tenantContext;

    public async Task<List<CurrencyDto>> GetActiveCurrenciesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching all active currencies");

        // AsNoTracking: read-only list — results are mapped to DTOs
        var currencies = await CurrencySet
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
        var query = CurrencySet.AsNoTracking().AsQueryable();

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
        var currency = await CurrencySet
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == currencyId, cancellationToken);

        return currency == null ? null : MapToDto(currency);
    }

    public async Task<CurrencyDto?> GetCurrencyByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        // AsNoTracking: read-only lookup — result is mapped to DTO
        var currency = await CurrencySet
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Code.ToLower() == code.ToLower(), cancellationToken);

        return currency == null ? null : MapToDto(currency);
    }

    public async Task<CurrencyDto> CreateCurrencyAsync(CreateCurrencyDto createDto, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating new currency: {Code}", createDto.Code);

        // Check if currency code already exists
        var existingCurrency = await CurrencySet
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

        CurrencySet.Add(currency);
        await ActiveContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created currency {Code} with ID {Id}", currency.Code, currency.Id);

        return MapToDto(currency);
    }

    public async Task<CurrencyDto?> UpdateCurrencyAsync(long currencyId, UpdateCurrencyDto updateDto, CancellationToken cancellationToken = default)
    {
        var currency = await CurrencySet
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

        await ActiveContext.SaveChangesAsync(cancellationToken);

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
    /// Soft-deletes a currency by setting IsActive = false.
    /// This is safe even when the currency is referenced by invoices or clients,
    /// because the record remains in the database — only hidden from active lists.
    /// </summary>
    public async Task<bool> DeleteCurrencyAsync(long currencyId, CancellationToken cancellationToken = default)
    {
        var currency = await CurrencySet
            .FirstOrDefaultAsync(c => c.Id == currencyId, cancellationToken);

        if (currency == null)
            return false;

        _logger.LogInformation("Soft-deleting currency {Code} (ID: {Id})", currency.Code, currency.Id);

        // Soft delete — deactivate instead of removing from database
        currency.IsActive = false;
        await ActiveContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Soft-deleted currency {Code} (ID: {Id})", currency.Code, currency.Id);

        return true;
    }

}
