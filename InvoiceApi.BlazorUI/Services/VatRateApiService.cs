using InvoiceApi.Application.Dto.VatRate;
using Microsoft.AspNetCore.Components.Authorization;

namespace InvoiceApi.BlazorUI.Services;

/// <summary>
/// Service for consuming VAT Rate API endpoints.
/// Supports impersonation — when SysAdmin selects a company, API data is filtered accordingly.
/// </summary>
public class VatRateApiService : ApiClientBase
{
    public VatRateApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<VatRateApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Gets all VAT rates
    /// </summary>
    public async Task<List<VatRateDto>> GetAllAsync(bool includeInactive = false)
    {
        var endpoint = $"/api/vatrate?includeInactive={includeInactive}";
        var result = await GetAsync<List<VatRateDto>>(endpoint);
        return result ?? new List<VatRateDto>();
    }

    /// <summary>
    /// Gets a specific VAT rate by ID
    /// </summary>
    public async Task<VatRateDto?> GetByIdAsync(long id)
    {
        return await GetAsync<VatRateDto>($"/api/vatrate/{id}");
    }

    /// <summary>
    /// Gets active VAT rates valid at a specific date
    /// </summary>
    public async Task<List<VatRateDto>> GetActiveForDateAsync(DateTime? date = null)
    {
        var dateParam = date.HasValue ? $"?date={date.Value:yyyy-MM-ddTHH:mm:ss}" : "";
        var endpoint = $"/api/vatrate/active{dateParam}";
        var result = await GetAsync<List<VatRateDto>>(endpoint);
        return result ?? new List<VatRateDto>();
    }

    /// <summary>
    /// Gets the default standard VAT rate
    /// </summary>
    public async Task<VatRateDto?> GetDefaultStandardAsync()
    {
        return await GetAsync<VatRateDto>("/api/vatrate/default/standard");
    }

    /// <summary>
    /// Gets the default reduced VAT rate
    /// </summary>
    public async Task<VatRateDto?> GetDefaultReducedAsync()
    {
        return await GetAsync<VatRateDto>("/api/vatrate/default/reduced");
    }

    /// <summary>
    /// Creates a new VAT rate
    /// </summary>
    public async Task<VatRateDto?> CreateAsync(CreateVatRateDto createDto)
    {
        return await PostAsync<CreateVatRateDto, VatRateDto>("/api/vatrate", createDto);
    }

    /// <summary>
    /// Updates an existing VAT rate
    /// </summary>
    public async Task<VatRateDto?> UpdateAsync(long id, UpdateVatRateDto updateDto)
    {
        return await PutAsync<UpdateVatRateDto, VatRateDto>($"/api/vatrate/{id}", updateDto);
    }

    /// <summary>
    /// Deletes a VAT rate (soft delete)
    /// </summary>
    public async Task<bool> DeleteAsync(long id)
    {
        return await DeleteAsync($"/api/vatrate/{id}");
    }

    /// <summary>
    /// Sets a VAT rate as default
    /// </summary>
    public async Task<VatRateDto?> SetAsDefaultAsync(long id)
    {
        return await PostAsync<object, VatRateDto>($"/api/vatrate/{id}/set-default", new { });
    }
}
