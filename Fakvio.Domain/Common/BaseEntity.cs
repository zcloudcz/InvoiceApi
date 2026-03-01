namespace Fakvio.Domain.Common;

/// <summary>
/// Base class for all database entities.
/// Contains common properties that every entity should have.
///
/// ARCHITECTURE NOTE: Soft-delete (IsActive / IsDeleted) is intentionally NOT included here.
/// Not all entities need soft-delete (e.g., InvoiceItem, Address, Contact, BillingSettings).
/// Entities that need soft-delete implement it individually:
///   - Invoice uses Status = EInvoiceStatus.Deleted (semantic status, not boolean)
///   - InvoiceTemplate uses IsActive (boolean toggle)
///   - Currency uses IsActive (boolean toggle)
/// This per-entity approach avoids forcing soft-delete columns on entities that don't need them
/// and allows each entity to use the deletion strategy that fits its domain semantics.
/// </summary>
public abstract class BaseEntity
{
    /// <summary>
    /// Unique identifier for this entity
    /// Generated automatically by database
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// When was this record created
    /// Set automatically on insert
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When was this record last modified
    /// Updated automatically on every update
    /// </summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// ID of user who created this record
    /// Useful for multi-user systems and audit trails
    /// </summary>
    public long? CreatedByUserId { get; set; }

    /// <summary>
    /// ID of user who last modified this record
    /// Useful for multi-user systems and audit trails
    /// </summary>
    public long? UpdatedByUserId { get; set; }
}
