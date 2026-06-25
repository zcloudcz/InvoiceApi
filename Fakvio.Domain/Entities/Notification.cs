using Fakvio.Domain.Common;
using Fakvio.Domain.Enums;

namespace Fakvio.Domain.Entities;

/// <summary>
/// A single notification event (e.g., "payment matched").
/// One Notification is created per business event; individual users get
/// their own <see cref="NotificationRecipient"/> rows that track read state.
/// </summary>
public class Notification : BaseEntity
{
    /// <summary>
    /// What kind of event triggered this notification.
    /// </summary>
    public ENotificationType Type { get; set; }

    /// <summary>
    /// Short headline shown in the notification list (e.g., "Platba spárována").
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Detailed description of the event (e.g., "Transakce VS 123 spárována s fakturou FV-2026001.").
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// PK of the entity this notification refers to (e.g., Invoice.Id).
    /// Used to build a navigation link in the UI.
    /// </summary>
    public long RelatedEntityId { get; set; }

    /// <summary>
    /// Type name of the related entity (e.g., "Invoice", "ReceivedInvoice").
    /// Combined with <see cref="RelatedEntityId"/> to construct the detail URL.
    /// </summary>
    public string RelatedEntityType { get; set; } = string.Empty;

    /// <summary>
    /// Per-user read/unread state for this notification.
    /// </summary>
    public ICollection<NotificationRecipient> Recipients { get; set; } = new List<NotificationRecipient>();
}
