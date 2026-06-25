using Fakvio.Contracts.Common.Pagination;
using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.Notification;

/// <summary>
/// Filter parameters for paginated notification listing.
/// All filters are optional — null means "no filter".
/// </summary>
public class NotificationFilterDto : PaginationParams
{
    /// <summary>
    /// When true, return only unread notifications.
    /// </summary>
    public bool? UnreadOnly { get; set; }

    /// <summary>
    /// Filter by notification type.
    /// </summary>
    public ENotificationType? Type { get; set; }
}
