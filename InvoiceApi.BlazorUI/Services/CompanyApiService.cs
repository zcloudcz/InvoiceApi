using System.Text;
using InvoiceApi.Application.Dto.Client;
using InvoiceApi.BlazorUI.Models;
using Microsoft.AspNetCore.Components.Authorization;

namespace InvoiceApi.BlazorUI.Services;

/// <summary>
/// Blazor service for communicating with the Company API endpoints.
/// Companies are issuers (the user's own companies that issue invoices).
/// Inherits ApiClientBase for shared auth, logging, impersonation, and error handling.
/// </summary>
public class CompanyApiService : ApiClientBase
{
    public CompanyApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<CompanyApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Gets all companies (issuers).
    /// </summary>
    public async Task<List<ClientDto>> GetAllAsync()
    {
        var result = await GetAsync<List<ClientDto>>("/api/company");
        return result ?? new List<ClientDto>();
    }

    /// <summary>
    /// Gets companies with server-side pagination, filtering, and sorting.
    /// </summary>
    public async Task<PagedResult<ClientDto>> GetPagedAsync(
        int page = 1,
        int pageSize = 50,
        string? search = null,
        string? sortBy = null,
        string? sortDirection = "asc",
        bool? isVatPayer = null,
        bool includeInactive = false,
        string? city = null,
        string? country = null)
    {
        var queryParams = new StringBuilder($"?Page={page}&PageSize={pageSize}");

        if (!string.IsNullOrWhiteSpace(search))
            queryParams.Append($"&Search={Uri.EscapeDataString(search)}");

        if (!string.IsNullOrWhiteSpace(sortBy))
            queryParams.Append($"&SortBy={sortBy}");

        if (!string.IsNullOrWhiteSpace(sortDirection))
            queryParams.Append($"&SortDirection={sortDirection}");

        if (isVatPayer.HasValue)
            queryParams.Append($"&IsVatPayer={isVatPayer.Value}");

        if (includeInactive)
            queryParams.Append("&IncludeInactive=true");

        if (!string.IsNullOrWhiteSpace(city))
            queryParams.Append($"&City={Uri.EscapeDataString(city)}");

        if (!string.IsNullOrWhiteSpace(country))
            queryParams.Append($"&Country={Uri.EscapeDataString(country)}");

        var result = await GetAsync<PagedResult<ClientDto>>($"/api/company/paged{queryParams}");
        return result ?? new PagedResult<ClientDto>();
    }

    /// <summary>
    /// Gets a single company by ID.
    /// </summary>
    public async Task<ClientDto?> GetByIdAsync(long id)
    {
        return await GetAsync<ClientDto>($"/api/company/{id}");
    }

    /// <summary>
    /// Creates a new company (issuer).
    /// </summary>
    public async Task<ClientDto?> CreateAsync(CreateClientDto createDto)
    {
        return await PostAsync<CreateClientDto, ClientDto>("/api/company", createDto);
    }

    /// <summary>
    /// Updates an existing company.
    /// </summary>
    public async Task<ClientDto?> UpdateAsync(long id, UpdateClientDto updateDto)
    {
        return await PutAsync<UpdateClientDto, ClientDto>($"/api/company/{id}", updateDto);
    }

    /// <summary>
    /// Deletes a company (soft delete).
    /// </summary>
    public async Task<bool> DeleteAsync(long id)
    {
        return await DeleteAsync($"/api/company/{id}");
    }

    /// <summary>
    /// Fetches company data from the Czech ARES registry by registration number (IČO).
    /// </summary>
    public async Task<ClientDto?> FetchFromAresAsync(string registrationNumber)
    {
        return await GetAsync<ClientDto>($"/api/company/ares/{registrationNumber}");
    }
}
