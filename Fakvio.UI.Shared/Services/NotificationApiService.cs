using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Notification;
using Fakvio.Domain.Enums;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor-side client for the notification API (NotificationController on the server).
/// Uses the shared <see cref="ApiClientBase"/> machinery so impersonation,
/// auth refresh, and typed error handling come for free.
/// </summary>
public class NotificationApiService : ApiClientBase
{
    public NotificationApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<NotificationApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Returns paginated notifications for the current user.
    /// GET /api/notification?page=1&amp;pageSize=20&amp;unreadOnly=true&amp;type=1
    /// </summary>
    public async Task<PagedResult<NotificationDto>> GetNotificationsAsync(
        int page = 1,
        int pageSize = 20,
        bool? unreadOnly = null,
        ENotificationType? type = null)
    {
        var url = $"/api/notification?page={page}&pageSize={pageSize}";
        if (unreadOnly.HasValue)
            url += $"&unreadOnly={unreadOnly.Value}";
        if (type.HasValue)
            url += $"&type={(int)type.Value}";

        try
        {
            return await GetAsync<PagedResult<NotificationDto>>(url)
                   ?? new PagedResult<NotificationDto>([], 0, page, pageSize);
        }
        catch (ApiException)
        {
            return new PagedResult<NotificationDto>([], 0, page, pageSize);
        }
    }

    /// <summary>
    /// Returns the unread notification count for the bell icon badge.
    /// GET /api/notification/unread-count
    /// </summary>
    public async Task<int> GetUnreadCountAsync()
    {
        try
        {
            return await GetAsync<int>("/api/notification/unread-count");
        }
        catch (ApiException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Returns a dashboard summary: unread count + up to 10 recent notifications.
    /// GET /api/notification/dashboard
    /// </summary>
    public async Task<NotificationDashboardDto?> GetDashboardAsync()
    {
        try
        {
            return await GetAsync<NotificationDashboardDto>("/api/notification/dashboard");
        }
        catch (ApiException)
        {
            return null;
        }
    }

    /// <summary>
    /// Marks a single notification as read.
    /// POST /api/notification/{id}/read
    /// </summary>
    public async Task MarkAsReadAsync(long notificationId)
    {
        try
        {
            await PostWithoutBodyAsync<object>($"/api/notification/{notificationId}/read");
        }
        catch (ApiException)
        {
            // Swallow — read state is best-effort.
        }
    }

    /// <summary>
    /// Marks all unread notifications as read.
    /// POST /api/notification/read-all
    /// </summary>
    public async Task MarkAllAsReadAsync()
    {
        try
        {
            await PostWithoutBodyAsync<object>("/api/notification/read-all");
        }
        catch (ApiException)
        {
            // Swallow — read state is best-effort.
        }
    }
}
