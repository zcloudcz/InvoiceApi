using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Notification;
using Fakvio.Domain.Enums;

namespace Fakvio.Application.Service;

/// <summary>
/// Manages in-app notifications within a tenant.
///
/// Notifications are informational messages targeted at individual users
/// (e.g., "payment matched on invoice X"). Unlike Alerts (which are tenant-wide
/// and have a resolve lifecycle), notifications are per-user with read/unread state.
///
/// Usage: call <see cref="CreateForAllUsersAsync"/> from the service that detects
/// the condition. All active users of the tenant receive their own copy.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Creates a notification and fans it out to all active users of the current tenant.
    /// Queries MasterDbContext for active users with matching CompanyId.
    /// </summary>
    /// <param name="type">Type of notification (e.g., PaymentMatched).</param>
    /// <param name="title">Short headline shown in the notification list.</param>
    /// <param name="message">Detailed description of the event.</param>
    /// <param name="relatedEntityId">PK of the related entity (for navigation).</param>
    /// <param name="relatedEntityType">Entity type name (e.g., "Invoice").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>ID of the created notification.</returns>
    Task<long> CreateForAllUsersAsync(
        ENotificationType type,
        string title,
        string message,
        long relatedEntityId,
        string relatedEntityType,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the number of unread notifications for the given user.
    /// Used for the bell icon badge.
    /// </summary>
    Task<int> GetUnreadCountAsync(long userId, CancellationToken ct = default);

    /// <summary>
    /// Returns a paginated list of notifications for the given user.
    /// </summary>
    Task<PagedResult<NotificationDto>> GetForUserAsync(
        long userId,
        NotificationFilterDto filter,
        CancellationToken ct = default);

    /// <summary>
    /// Returns a dashboard summary: unread count + up to 10 recent notifications.
    /// </summary>
    Task<NotificationDashboardDto> GetDashboardAsync(long userId, CancellationToken ct = default);

    /// <summary>
    /// Marks a single notification as read for the given user.
    /// Idempotent — marking an already-read notification is a no-op.
    /// </summary>
    Task MarkAsReadAsync(long notificationId, long userId, CancellationToken ct = default);

    /// <summary>
    /// Marks all unread notifications as read for the given user.
    /// </summary>
    Task MarkAllAsReadAsync(long userId, CancellationToken ct = default);
}
