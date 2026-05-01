using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Currency;

namespace Fakvio.Application.Service;

/// <summary>
/// Exception thrown when no exchange rate record can be found for the
/// requested currency and date (including fallback to previous days).
/// </summary>
public class ExchangeRateNotFoundException : Exception
{
    public string CurrencyCode { get; }
    public DateOnly Date { get; }

    public ExchangeRateNotFoundException(string currencyCode, DateOnly date)
        : base($"No exchange rate found for currency '{currencyCode}' on or before {date:yyyy-MM-dd}.")
    {
        CurrencyCode = currencyCode;
        Date = date;
    }
}

/// <summary>
/// Service interface for currency management and CZK conversion.
/// CRUD operations are only accessible by SysAdmin role;
/// <see cref="ConvertToCzkAsync"/> is available to all authenticated callers.
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
    /// Converts an amount from a foreign currency to CZK using the exchange rate
    /// valid on the given date (DUZP — date of taxable supply).
    ///
    /// Algorithm:
    /// <code>czk = amount * (rate.Rate / rate.Amount)</code>
    ///
    /// Fallback: if no rate exists for the exact date, the most recent rate
    /// with ValidFrom &lt;= <paramref name="date"/> is used (handles weekends / holidays).
    ///
    /// Special case: if <paramref name="currencyCode"/> is "CZK", returns <paramref name="amount"/>
    /// unchanged (no conversion needed).
    /// </summary>
    /// <param name="amount">Amount in <paramref name="currencyCode"/> to convert.</param>
    /// <param name="currencyCode">ISO 4217 code of the source currency (e.g., "EUR").</param>
    /// <param name="date">Date of taxable supply (DUZP) — determines which rate to use.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Equivalent amount in CZK, rounded to 2 decimal places.</returns>
    /// <exception cref="ExchangeRateNotFoundException">
    /// Thrown when no rate is available for <paramref name="currencyCode"/> on or before <paramref name="date"/>.
    /// </exception>
    Task<decimal> ConvertToCzkAsync(
        decimal amount,
        string currencyCode,
        DateOnly date,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the exchange rate record valid on the given date for the specified currency.
    /// Same fallback logic as <see cref="ConvertToCzkAsync"/> (most recent &lt;= date).
    /// </summary>
    /// <param name="currencyCode">ISO 4217 code (e.g., "EUR"). "CZK" returns null.</param>
    /// <param name="date">Target date.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching <see cref="ExchangeRateDto"/>, or null for CZK.</returns>
    Task<ExchangeRateDto?> GetExchangeRateAsync(
        string currencyCode,
        DateOnly date,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches today's CNB rates and saves new records to the tenant's ExchangeRate table.
    /// Idempotent — records already present for today are skipped.
    /// Used by the manual "Refresh from CNB" button in the UI.
    /// </summary>
    /// <returns>Number of new records inserted.</returns>
    Task<int> RefreshExchangeRatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the exchange rate auto-update settings for the current company.
    /// </summary>
    Task<ExchangeRateSettingsDto> GetExchangeRateSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the exchange rate auto-update settings for the current company.
    /// </summary>
    Task<ExchangeRateSettingsDto> UpdateExchangeRateSettingsAsync(
        UpdateExchangeRateSettingsDto dto,
        CancellationToken cancellationToken = default);
}
