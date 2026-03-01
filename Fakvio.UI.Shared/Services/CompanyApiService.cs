using System.Text;
using Fakvio.Contracts.Dto.Client;
using Fakvio.UI.Shared.Models;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

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
        try
        {
            return await GetAsync<List<ClientDto>>("/api/company") ?? [];
        }
        catch (ApiException)
        {
            // Graceful degradation for list endpoints — show empty grid instead of crashing.
            // 401 is already handled by UnauthorizedRedirectHandler (redirects to /login).
            return [];
        }
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
        try
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

            return await GetAsync<PagedResult<ClientDto>>($"/api/company/paged{queryParams}")
                   ?? new PagedResult<ClientDto>();
        }
        catch (ApiException)
        {
            // Graceful degradation for list endpoints — show empty grid instead of crashing.
            // 401 is already handled by UnauthorizedRedirectHandler (redirects to /login).
            return new PagedResult<ClientDto>();
        }
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
