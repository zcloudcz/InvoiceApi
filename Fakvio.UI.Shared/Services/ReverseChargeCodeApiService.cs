using Fakvio.Contracts.Dto.ReverseChargeCode;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Client-side service for consuming the reverse charge code API endpoint.
///
/// Reverse charge codes (kódy předmětu plnění PDP) are read-only MFČR reference data
/// used to populate the dropdown in the invoice line-item editor and the admin grid (#49).
///
/// All methods are GET-only — admin CRUD lives in the separate admin page (task #49).
/// Inherits ApiClientBase for shared JWT auth, logging, and SysAdmin impersonation support.
/// </summary>
public class ReverseChargeCodeApiService : ApiClientBase
{
    public ReverseChargeCodeApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<ReverseChargeCodeApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Fetches all currently active reverse charge codes ordered by Code ascending.
    ///
    /// Returns empty list on error — dropdown will show "no items" instead of crashing the page.
    /// 401 responses are handled by UnauthorizedRedirectHandler (redirects to /login).
    /// </summary>
    /// <returns>Active reverse charge codes for use in the PDP dropdown.</returns>
    public async Task<List<ReverseChargeCodeDto>> GetAllActiveAsync()
    {
        try
        {
            return await GetAsync<List<ReverseChargeCodeDto>>("/api/reversechargecode") ?? [];
        }
        catch (ApiException)
        {
            // Graceful degradation — show empty dropdown rather than crashing the invoice editor.
            // 401 is already handled by UnauthorizedRedirectHandler.
            return [];
        }
    }

    /// <summary>
    /// Fetches a single reverse charge code by its database ID.
    ///
    /// Returns null when the code is not found (404) or on other errors.
    /// Used by admin edit pages (task #49) to pre-populate the edit form.
    /// </summary>
    /// <param name="id">Database primary key (BaseEntity.Id).</param>
    /// <returns>The matching code DTO, or null if not found.</returns>
    public async Task<ReverseChargeCodeDto?> GetByIdAsync(long id)
    {
        return await GetAsync<ReverseChargeCodeDto>($"/api/reversechargecode/{id}");
    }
}
