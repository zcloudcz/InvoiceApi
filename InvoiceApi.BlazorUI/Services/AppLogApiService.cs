using InvoiceApi.Contracts.Common.Pagination;
using InvoiceApi.Contracts.Dto.AppLog;
using Microsoft.AspNetCore.Components.Authorization;

namespace InvoiceApi.BlazorUI.Services;

/// <summary>
/// Blazor API client for viewing application logs.
/// Calls the /api/logs endpoints on the backend.
/// SysAdmin-only — used by the Logs.razor page and SysAdmin dashboard.
/// </summary>
public class AppLogApiService : ApiClientBase
{
    public AppLogApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<AppLogApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Gets paginated log entries with optional filtering.
    /// </summary>
    public async Task<PagedResult<AppLogDto>?> GetPagedAsync(
        int page = 1,
        int pageSize = 50,
        string? level = null,
        string? search = null,
        DateTime? from = null,
        DateTime? to = null)
    {
        var url = $"/api/logs/paged?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(level)) url += $"&level={Uri.EscapeDataString(level)}";
        if (!string.IsNullOrWhiteSpace(search)) url += $"&search={Uri.EscapeDataString(search)}";
        if (from.HasValue) url += $"&from={from.Value:O}";
        if (to.HasValue) url += $"&to={to.Value:O}";

        return await GetAsync<PagedResult<AppLogDto>>(url);
    }

    /// <summary>
    /// Gets log count summary by level — for the SysAdmin dashboard.
    /// </summary>
    public async Task<AppLogSummaryDto?> GetSummaryAsync()
        => await GetAsync<AppLogSummaryDto>("/api/logs/summary");
}
