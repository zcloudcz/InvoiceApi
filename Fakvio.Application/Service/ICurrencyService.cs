using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Currency;

namespace Fakvio.Application.Service;

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

    /// <summary>
    /// Converts an amount in the given foreign currency to CZK using the ČNB exchange rate
    /// for the specified date (DUZP — date of taxable supply).
    ///
    /// If <paramref name="currencyCode"/> is already "CZK", returns <paramref name="amount"/> unchanged.
    /// When <c>storedRate</c> (CZK per 1 unit, from the document) is given it is used; otherwise the ČNB rate for the date. Throws ExchangeRateUnavailableException when no rate can be determined (never returns the foreign amount as CZK).
    ///
    /// Used by the EPO DPHDP3 generator to normalise non-CZK invoice amounts before
    /// mapping them to the XML rows (all EPO amounts are reported in whole CZK).
    /// </summary>
    /// <param name="amount">Amount in <paramref name="currencyCode"/>.</param>
    /// <param name="currencyCode">ISO 4217 code, e.g. "EUR", "USD".</param>
    /// <param name="date">Date of taxable supply (DUZP) for which to look up the ČNB rate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Equivalent amount in CZK, rounded to 2 decimal places.</returns>
    Task<decimal> ConvertToCzkAsync(
        decimal amount,
        string currencyCode,
        DateOnly date,
        CancellationToken cancellationToken = default,
        decimal? storedRate = null);
}
