using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Reminder;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// API client for the Reminder (dunning) system.
/// Consumes endpoints from ReminderController.
/// Handles settings CRUD, reminder listing, send/cancel actions, and dashboard data.
/// </summary>
public class ReminderApiService : ApiClientBase
{
    public ReminderApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<ReminderApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    // ─── Settings ───────────────────────────────────────────────────────

    /// <summary>
    /// Gets company-level default reminder settings.
    /// GET /api/reminder/settings
    /// </summary>
    public async Task<ReminderSettingsDto?> GetCompanySettingsAsync()
    {
        return await GetAsync<ReminderSettingsDto>("/api/reminder/settings");
    }

    /// <summary>
    /// Gets effective settings for a specific client (client override or company default).
    /// GET /api/reminder/settings/client/{clientId}
    /// </summary>
    public async Task<ReminderSettingsDto?> GetClientSettingsAsync(long clientId)
    {
        return await GetAsync<ReminderSettingsDto>($"/api/reminder/settings/client/{clientId}");
    }

    /// <summary>
    /// Creates or updates reminder settings (company default or client override).
    /// PUT /api/reminder/settings
    /// </summary>
    public async Task<ReminderSettingsDto?> UpsertSettingsAsync(UpdateReminderSettingsDto dto)
    {
        return await PutAsync<UpdateReminderSettingsDto, ReminderSettingsDto>("/api/reminder/settings", dto);
    }

    /// <summary>
    /// Deletes a client-level override.
    /// DELETE /api/reminder/settings/client/{clientId}
    /// </summary>
    public async Task<bool> DeleteClientSettingsAsync(long clientId)
    {
        return await DeleteAsync($"/api/reminder/settings/client/{clientId}");
    }

    /// <summary>
    /// Lists all client-level overrides.
    /// GET /api/reminder/settings/overrides
    /// </summary>
    public async Task<List<ReminderSettingsDto>> GetClientOverridesAsync()
    {
        try
        {
            return await GetAsync<List<ReminderSettingsDto>>("/api/reminder/settings/overrides") ?? [];
        }
        catch (ApiException)
        {
            return [];
        }
    }

    // ─── Reminders ──────────────────────────────────────────────────────

    /// <summary>
    /// Lists reminders with pagination and filtering.
    /// GET /api/reminder/paged
    /// </summary>
    public async Task<PagedResult<ReminderDto>?> GetPagedAsync(ReminderFilterDto filter)
    {
        var url = $"/api/reminder/paged?page={filter.Page}&pageSize={filter.PageSize}";

        if (!string.IsNullOrEmpty(filter.Search))
            url += $"&search={Uri.EscapeDataString(filter.Search)}";
        if (filter.Status.HasValue)
            url += $"&status={filter.Status.Value}";
        if (filter.ClientId.HasValue)
            url += $"&clientId={filter.ClientId.Value}";
        if (filter.InvoiceId.HasValue)
            url += $"&invoiceId={filter.InvoiceId.Value}";
        if (filter.Level.HasValue)
            url += $"&level={filter.Level.Value}";
        if (filter.DateFrom.HasValue)
            url += $"&dateFrom={filter.DateFrom.Value:yyyy-MM-dd}";
        if (filter.DateTo.HasValue)
            url += $"&dateTo={filter.DateTo.Value:yyyy-MM-dd}";
        if (!string.IsNullOrEmpty(filter.SortBy))
            url += $"&sortBy={filter.SortBy}";
        if (!string.IsNullOrEmpty(filter.SortDirection))
            url += $"&sortDirection={filter.SortDirection}";

        return await GetAsync<PagedResult<ReminderDto>>(url);
    }

    /// <summary>
    /// Lists all reminders for a specific invoice.
    /// GET /api/reminder/invoice/{invoiceId}
    /// </summary>
    public async Task<List<ReminderDto>> GetByInvoiceAsync(long invoiceId)
    {
        try
        {
            return await GetAsync<List<ReminderDto>>($"/api/reminder/invoice/{invoiceId}") ?? [];
        }
        catch (ApiException)
        {
            return [];
        }
    }

    /// <summary>
    /// Manually sends a Draft reminder.
    /// POST /api/reminder/{id}/send
    /// </summary>
    public async Task<ReminderDto?> SendAsync(long id)
    {
        return await PostWithoutBodyAsync<ReminderDto>($"/api/reminder/{id}/send");
    }

    /// <summary>
    /// Cancels a reminder with optional notes.
    /// POST /api/reminder/{id}/cancel
    /// </summary>
    public async Task<ReminderDto?> CancelAsync(long id, string? notes = null)
    {
        var url = $"/api/reminder/{id}/cancel";
        if (!string.IsNullOrEmpty(notes))
            url += $"?notes={Uri.EscapeDataString(notes)}";

        return await PostWithoutBodyAsync<ReminderDto>(url);
    }

    // ─── Dashboard ──────────────────────────────────────────────────────

    /// <summary>
    /// Gets dashboard summary data for the reminder widget.
    /// GET /api/reminder/dashboard
    /// </summary>
    public async Task<ReminderDashboardDto?> GetDashboardDataAsync()
    {
        try
        {
            return await GetAsync<ReminderDashboardDto>("/api/reminder/dashboard");
        }
        catch (ApiException)
        {
            return null;
        }
    }
}
