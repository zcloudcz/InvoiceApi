using System.Text;
using Fakvio.Contracts.Dto.Client;
using Fakvio.UI.Shared.Models;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor service for communicating with the Client API endpoints.
/// Inherits ApiClientBase for shared auth, logging, impersonation, and error handling.
/// </summary>
public class ClientApiService : ApiClientBase
{
    public ClientApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<ClientApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Gets all clients (non-paginated). Optionally includes inactive clients.
    /// </summary>
    public async Task<List<ClientDto>> GetAllAsync(bool includeInactive = false)
    {
        try
        {
            return await GetAsync<List<ClientDto>>($"/api/client?includeInactive={includeInactive}") ?? [];
        }
        catch (ApiException)
        {
            // Graceful degradation for list endpoints — show empty grid instead of crashing.
            // 401 is already handled by UnauthorizedRedirectHandler (redirects to /login).
            return [];
        }
    }

    /// <summary>
    /// Gets clients with server-side pagination, filtering, and sorting.
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

            return await GetAsync<PagedResult<ClientDto>>($"/api/client/paged{queryParams}")
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
    /// Gets a single client by ID.
    /// </summary>
    public async Task<ClientDto?> GetByIdAsync(long id)
    {
        return await GetAsync<ClientDto>($"/api/client/{id}");
    }

    /// <summary>
    /// Creates a new client.
    /// </summary>
    public async Task<ClientDto?> CreateAsync(CreateClientDto createDto)
    {
        return await PostAsync<CreateClientDto, ClientDto>("/api/client", createDto);
    }

    /// <summary>
    /// Updates an existing client.
    /// </summary>
    public async Task<ClientDto?> UpdateAsync(long id, UpdateClientDto updateDto)
    {
        return await PutAsync<UpdateClientDto, ClientDto>($"/api/client/{id}", updateDto);
    }

    /// <summary>
    /// Deletes a client (soft delete).
    /// </summary>
    public async Task<bool> DeleteAsync(long id)
    {
        return await DeleteAsync($"/api/client/{id}");
    }

    /// <summary>
    /// Gets the issuer (the current user's company) via GET /api/client/issuer.
    /// The API automatically resolves the issuer based on the authenticated user's CompanyId.
    /// Returns null if no issuer is configured.
    /// </summary>
    public async Task<ClientDto?> GetIssuerAsync()
    {
        return await GetAsync<ClientDto>("/api/client/issuer");
    }

    /// <summary>
    /// Fetches company data from the Czech ARES registry by registration number (IČO).
    /// Returns a pre-filled ClientDto or null if not found.
    /// </summary>
    public async Task<ClientDto?> FetchFromAresAsync(string registrationNumber)
    {
        return await GetAsync<ClientDto>($"/api/client/ares/{registrationNumber}");
    }
}
