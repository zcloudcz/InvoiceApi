using Fakvio.Contracts.Dto.Dashboard;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor service that communicates with the Dashboard API endpoint.
/// Fetches aggregated statistics for the home page (invoice counts, client counts, etc.).
/// Inherits ApiClientBase for shared auth, logging, impersonation, and error handling.
/// </summary>
public class DashboardApiService : ApiClientBase
{
    public DashboardApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<DashboardApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Fetches all dashboard statistics from the API.
    /// Returns a DashboardDto with aggregated statistics, or a default empty DTO on error.
    /// </summary>
    public async Task<DashboardDto> GetDashboardAsync()
    {
        try
        {
            return await GetAsync<DashboardDto>("/api/dashboard") ?? new DashboardDto();
        }
        catch (ApiException)
        {
            // Graceful degradation for dashboard aggregation — show empty stats instead of crashing.
            // 401 is already handled by UnauthorizedRedirectHandler (redirects to /login).
            return new DashboardDto();
        }
    }

    /// <summary>
    /// Fetches SysAdmin-specific dashboard data (company stats, recent logs).
    /// Returns null if the endpoint fails (e.g., non-SysAdmin user).
    /// </summary>
    public async Task<SysAdminDashboardDto?> GetSysAdminDashboardAsync()
    {
        return await GetAsync<SysAdminDashboardDto>("/api/dashboard/sysadmin");
    }
}
