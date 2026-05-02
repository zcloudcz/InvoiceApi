using Fakvio.Contracts.Dto.Currency;
using Fakvio.UI.Shared.Models;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor service for communicating with the Currency API endpoints.
/// Inherits ApiClientBase for shared auth, logging, impersonation, and error handling.
/// Most endpoints require SysAdmin role; GetActiveCurrenciesAsync is available to all authenticated users.
/// </summary>
public class CurrencyApiService : ApiClientBase
{
    public CurrencyApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<CurrencyApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Gets all active currencies sorted by SortOrder.
    /// Available to all authenticated users — used in dropdowns for invoices, templates, etc.
    /// </summary>
    public async Task<List<CurrencyDto>> GetActiveCurrenciesAsync()
    {
        try
        {
            return await GetAsync<List<CurrencyDto>>("/api/currency/active") ?? [];
        }
        catch (ApiException)
        {
            // Graceful degradation for list endpoints — show empty grid instead of crashing.
            // 401 is already handled by UnauthorizedRedirectHandler (redirects to /login).
            return [];
        }
    }

    /// <summary>
    /// Gets currencies with server-side pagination, filtering, and sorting.
    /// SysAdmin only — used on the Currencies admin page.
    /// </summary>
    public async Task<PagedResult<CurrencyDto>> GetPagedAsync(
        int page = 1,
        int pageSize = 10,
        string? search = null,
        bool? isActive = null,
        string sortBy = "SortOrder",
        bool isDescending = false)
    {
        try
        {
            var endpoint = $"/api/currency?page={page}&pageSize={pageSize}" +
                           $"&search={Uri.EscapeDataString(search ?? "")}" +
                           $"&isActive={isActive}" +
                           $"&sortBy={sortBy}&isDescending={isDescending}";

            return await GetAsync<PagedResult<CurrencyDto>>(endpoint) ?? new PagedResult<CurrencyDto>();
        }
        catch (ApiException)
        {
            // Graceful degradation for list endpoints — show empty grid instead of crashing.
            // 401 is already handled by UnauthorizedRedirectHandler (redirects to /login).
            return new PagedResult<CurrencyDto>();
        }
    }

    /// <summary>
    /// Gets a single currency by ID.
    /// </summary>
    public async Task<CurrencyDto?> GetByIdAsync(long id)
    {
        return await GetAsync<CurrencyDto>($"/api/currency/{id}");
    }

    /// <summary>
    /// Creates a new currency. SysAdmin only.
    /// </summary>
    public async Task<CurrencyDto?> CreateAsync(CreateCurrencyDto createDto)
    {
        return await PostAsync<CreateCurrencyDto, CurrencyDto>("/api/currency", createDto);
    }

    /// <summary>
    /// Updates an existing currency. SysAdmin only.
    /// Currency code cannot be changed after creation.
    /// </summary>
    public async Task<CurrencyDto?> UpdateAsync(long id, UpdateCurrencyDto updateDto)
    {
        return await PutAsync<UpdateCurrencyDto, CurrencyDto>($"/api/currency/{id}", updateDto);
    }

    /// <summary>
    /// Deletes a currency (soft delete). SysAdmin only.
    /// Cannot delete a currency that is in use by invoices.
    /// </summary>
    public async Task<bool> DeleteAsync(long id)
    {
        return await DeleteAsync($"/api/currency/{id}");
    }

    /// <summary>
    /// Triggers an immediate CNB exchange rate refresh for the current tenant.
    /// </summary>
    public async Task<int> RefreshExchangeRatesAsync()
    {
        var result = await PostAsync<object, Dictionary<string, int>>("/api/currency/exchange-rates/refresh", new { });
        return result?.GetValueOrDefault("savedCount") ?? 0;
    }

    /// <summary>
    /// Gets the exchange rate auto-update settings for the current company.
    /// </summary>
    public async Task<ExchangeRateSettingsDto?> GetExchangeRateSettingsAsync()
    {
        return await GetAsync<ExchangeRateSettingsDto>("/api/currency/exchange-rates/settings");
    }

    /// <summary>
    /// Updates the exchange rate auto-update settings.
    /// </summary>
    public async Task<ExchangeRateSettingsDto?> UpdateExchangeRateSettingsAsync(UpdateExchangeRateSettingsDto dto)
    {
        return await PutAsync<UpdateExchangeRateSettingsDto, ExchangeRateSettingsDto>("/api/currency/exchange-rates/settings", dto);
    }
}
