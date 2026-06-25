namespace Fakvio.Contracts.Dto.Notification;

/// <summary>
/// Summary for the notification bell icon: unread count + recent items.
/// </summary>
public class NotificationDashboardDto
{
    /// <summary>Total number of unread notifications for the current user.</summary>
    public int UnreadCount { get; set; }

    /// <summary>
    /// Up to 10 most recent notifications, sorted by CreatedAt descending.
    /// </summary>
    public List<NotificationDto> RecentNotifications { get; set; } = [];
}
