using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.Notification;

/// <summary>
/// Read-model for a single notification returned by GET /api/notification.
/// </summary>
public class NotificationDto
{
    /// <summary>Notification primary key.</summary>
    public long Id { get; set; }

    /// <summary>Type of notification (e.g., PaymentMatched).</summary>
    public ENotificationType Type { get; set; }

    /// <summary>Short headline (e.g., "Platba spárována").</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Detailed description of the event.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>PK of the related entity for navigation.</summary>
    public long RelatedEntityId { get; set; }

    /// <summary>Type name of the related entity (e.g., "Invoice").</summary>
    public string RelatedEntityType { get; set; } = string.Empty;

    /// <summary>When the notification was created (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Whether the current user has read this notification.</summary>
    public bool IsRead { get; set; }

    /// <summary>When the current user read this notification — null if unread.</summary>
    public DateTime? ReadAt { get; set; }
}
