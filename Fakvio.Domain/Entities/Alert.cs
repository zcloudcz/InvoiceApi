using Fakvio.Domain.Common;
using Fakvio.Domain.Enums;

namespace Fakvio.Domain.Entities;

/// <summary>
/// A business alert that requires the user's attention.
/// Designed to be generic and extensible — new alert types are added via
/// the <see cref="EAlertType"/> enum without changing this entity.
///
/// Lifecycle: Created → (user resolves) → ResolvedAt is set, alert disappears
/// from the dashboard until the condition occurs again.
/// </summary>
public class Alert : BaseEntity
{
    /// <summary>
    /// What kind of alert this is (e.g., OverpaidProforma).
    /// Stored as int for forward-compatibility.
    /// </summary>
    public EAlertType Type { get; set; }

    /// <summary>
    /// Primary key of the entity that triggered this alert
    /// (e.g., Invoice.Id for OverpaidProforma).
    /// Used to navigate the user to the relevant detail page.
    /// </summary>
    public long RelatedEntityId { get; set; }

    /// <summary>
    /// Name of the entity type (e.g., "Invoice", "ReceivedInvoice").
    /// Kept as a string so no FK join is needed and the field works
    /// generically for any entity type.
    /// </summary>
    public string RelatedEntityType { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable description of the alert condition, stored in Czech.
    /// Example: "Přeplatek 1 250,00 CZK na proformě PF-2026001."
    /// Populated when the alert is created; not changed afterwards.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// When this alert was resolved (null = still open / unresolved).
    /// Set by POST /api/alert/{id}/resolve.
    /// </summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>
    /// ID of the user who resolved the alert (null if still open).
    /// Denormalized from the resolver's JWT claim for audit purposes.
    /// </summary>
    public long? ResolvedByUserId { get; set; }
}
