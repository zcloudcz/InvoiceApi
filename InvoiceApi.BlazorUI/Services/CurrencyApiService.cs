using InvoiceApi.Application.Dto.Currency;
using InvoiceApi.BlazorUI.Models;
using Microsoft.AspNetCore.Components.Authorization;

namespace InvoiceApi.BlazorUI.Services;

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
        var result = await GetAsync<List<CurrencyDto>>("/api/currency/active");
        return result ?? new List<CurrencyDto>();
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
        var endpoint = $"/api/currency?page={page}&pageSize={pageSize}" +
                       $"&search={Uri.EscapeDataString(search ?? "")}" +
                       $"&isActive={isActive}" +
                       $"&sortBy={sortBy}&isDescending={isDescending}";

        var result = await GetAsync<PagedResult<CurrencyDto>>(endpoint);
        return result ?? new PagedResult<CurrencyDto>();
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
}
