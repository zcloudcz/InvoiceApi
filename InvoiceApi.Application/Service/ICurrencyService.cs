using InvoiceApi.Contracts.Common.Pagination;
using InvoiceApi.Contracts.Dto.Currency;

namespace InvoiceApi.Application.Service;

/// <summary>
/// Service interface for currency management
/// Only accessible by SysAdmin role
/// </summary>
public interface ICurrencyService
{
    /// <summary>
    /// Get all active currencies
    /// Used for dropdowns in UI
    /// </summary>
    Task<List<CurrencyDto>> GetActiveCurrenciesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all currencies (including inactive) with pagination, filtering, and sorting
    /// </summary>
    Task<PagedResult<CurrencyDto>> GetCurrenciesPagedAsync(
        int page = 1,
        int pageSize = 10,
        string? search = null,
        bool? isActive = null,
        string sortBy = "SortOrder",
        bool isDescending = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get currency by ID
    /// </summary>
    Task<CurrencyDto?> GetCurrencyByIdAsync(long currencyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get currency by code (e.g., "CZK")
    /// </summary>
    Task<CurrencyDto?> GetCurrencyByCodeAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create a new currency
    /// Only SysAdmin can create currencies
    /// </summary>
    Task<CurrencyDto> CreateCurrencyAsync(CreateCurrencyDto createDto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update an existing currency
    /// Only SysAdmin can update currencies
    /// Currency code cannot be changed
    /// </summary>
    Task<CurrencyDto?> UpdateCurrencyAsync(long currencyId, UpdateCurrencyDto updateDto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete (soft delete - set IsActive = false) a currency
    /// Only SysAdmin can delete currencies
    /// Cannot delete currency that is used in invoices or as client preferred currency
    /// </summary>
    Task<bool> DeleteCurrencyAsync(long currencyId, CancellationToken cancellationToken = default);
}
