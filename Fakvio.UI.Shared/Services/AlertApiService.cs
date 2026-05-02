using Fakvio.Contracts.Dto.Alert;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor-side client for the tenant alert API (<see cref="AlertController"/> on the server).
/// Uses the shared <see cref="ApiClientBase"/> machinery so impersonation,
/// auth refresh, and typed error handling come for free.
/// </summary>
public class AlertApiService : ApiClientBase
{
    public AlertApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<AlertApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    // ─── Alerts ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns open alerts for the current tenant.
    /// GET /api/alert?unresolvedOnly=true
    /// </summary>
    public async Task<List<AlertDto>> GetAlertsAsync(bool unresolvedOnly = true)
    {
        try
        {
            return await GetAsync<List<AlertDto>>($"/api/alert?unresolvedOnly={unresolvedOnly}") ?? [];
        }
        catch (ApiException)
        {
            return [];
        }
    }

    /// <summary>
    /// Returns the dashboard summary tile data: count + up to 5 recent alerts.
    /// GET /api/alert/dashboard
    /// </summary>
    public async Task<AlertDashboardDto?> GetDashboardAsync()
    {
        try
        {
            return await GetAsync<AlertDashboardDto>("/api/alert/dashboard");
        }
        catch (ApiException)
        {
            return null;
        }
    }

    /// <summary>
    /// Marks an alert as resolved.
    /// POST /api/alert/{id}/resolve
    /// </summary>
    public async Task<AlertDto?> ResolveAsync(long alertId)
    {
        return await PostWithoutBodyAsync<AlertDto>($"/api/alert/{alertId}/resolve");
    }
}
