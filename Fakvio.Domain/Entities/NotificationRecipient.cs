using Fakvio.Domain.Common;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Links a <see cref="Notification"/> to a specific user and tracks read state.
/// One row per (Notification, User) pair. UserId is stored as a plain long
/// because User lives in MasterDbContext (no cross-context FK).
/// </summary>
public class NotificationRecipient : BaseEntity
{
    /// <summary>FK to the parent notification.</summary>
    public long NotificationId { get; set; }

    /// <summary>Navigation to the parent notification.</summary>
    public Notification Notification { get; set; } = null!;

    /// <summary>
    /// ID of the recipient user. Not an EF FK — User entity lives in MasterDbContext.
    /// Same pattern as <see cref="BaseEntity.CreatedByUserId"/>.
    /// </summary>
    public long UserId { get; set; }

    /// <summary>
    /// When the user read this notification (null = unread).
    /// </summary>
    public DateTime? ReadAt { get; set; }
}
