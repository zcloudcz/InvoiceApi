using Fakvio.Domain.Common;
using Fakvio.Domain.Enums;

namespace Fakvio.Domain.Entities;

/// <summary>
/// A single reminder record for an overdue invoice.
/// Created by the daily dunning job or manually by the user.
/// Tracks the escalation level, amounts (including fees and interest), and delivery status.
///
/// Each invoice can have multiple reminders (one per level), enforced by a unique index
/// on (InvoiceId, Level). For example, an invoice might have:
///   - Level 1 reminder (Sent, +7 days, no fee)
///   - Level 2 reminder (Sent, +21 days, 50 CZK fee)
///   - Level 3 reminder (Draft, +35 days, 200 CZK fee, pending manual review)
/// </summary>
public class Reminder : BaseEntity
{
    /// <summary>
    /// FK to the overdue Invoice this reminder relates to.
    /// </summary>
    public long InvoiceId { get; set; }

    /// <summary>
    /// Navigation property to the Invoice.
    /// </summary>
    public Invoice Invoice { get; set; } = null!;

    /// <summary>
    /// FK to the Client (debtor). Denormalized from Invoice for faster queries
    /// and filtering (e.g., "show all reminders for client X").
    /// </summary>
    public long ClientId { get; set; }

    /// <summary>
    /// Navigation property to the Client.
    /// </summary>
    public Client Client { get; set; } = null!;

    /// <summary>
    /// Escalation level of this reminder (1, 2, 3, ...).
    /// Combined with InvoiceId forms a unique constraint — one reminder per level per invoice.
    /// </summary>
    public int Level { get; set; }

    /// <summary>
    /// Current status of this reminder in its lifecycle.
    /// Draft → Sent (success) or Failed (error). Can also be Cancelled by user.
    /// </summary>
    public EReminderStatus Status { get; set; } = EReminderStatus.Draft;

    /// <summary>
    /// Original due date of the invoice (denormalized for display/reporting).
    /// </summary>
    public DateTime DueDate { get; set; }

    /// <summary>
    /// Date when this reminder was created (by the dunning job or manually).
    /// </summary>
    public DateTime ReminderDate { get; set; }

    /// <summary>
    /// Outstanding invoice amount at the time of reminder creation (denormalized).
    /// </summary>
    public decimal InvoiceAmount { get; set; }

    /// <summary>
    /// Fixed fee charged for this reminder level, in CZK.
    /// Copied from ReminderLevel.FixedFeeCzk at creation time.
    /// </summary>
    public decimal FeeCzk { get; set; }

    /// <summary>
    /// Calculated statutory late payment interest in CZK (§ 1970 OZ).
    /// Zero if IncludeInterest is disabled in settings.
    /// </summary>
    public decimal InterestCzk { get; set; }

    /// <summary>
    /// Total amount due: InvoiceAmount + FeeCzk + InterestCzk.
    /// This is the amount shown on the reminder to the client.
    /// </summary>
    public decimal TotalCzk { get; set; }

    /// <summary>
    /// When the reminder email was successfully sent. Null if Draft/Failed/Cancelled.
    /// </summary>
    public DateTime? SentAt { get; set; }

    /// <summary>
    /// Email address the reminder was sent to. Null if not yet sent.
    /// </summary>
    public string? SentToEmail { get; set; }

    /// <summary>
    /// Error message if sending failed (Status = Failed).
    /// Contains SMTP error or other delivery failure details.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Optional user notes (e.g., reason for cancellation, manual override explanation).
    /// </summary>
    public string? Notes { get; set; }
}
