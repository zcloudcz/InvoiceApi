using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.Reminder;

/// <summary>
/// Read DTO for a single reminder record.
/// Contains denormalized invoice/client info for display without extra lookups.
/// </summary>
public class ReminderDto
{
    /// <summary>
    /// Primary key of the reminder.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// FK to the overdue invoice.
    /// </summary>
    public long InvoiceId { get; set; }

    /// <summary>
    /// Invoice document number (denormalized for display).
    /// </summary>
    public string? InvoiceNumber { get; set; }

    /// <summary>
    /// FK to the client (debtor).
    /// </summary>
    public long ClientId { get; set; }

    /// <summary>
    /// Client company name (denormalized for display).
    /// </summary>
    public string? ClientName { get; set; }

    /// <summary>
    /// Escalation level (1, 2, 3, ...).
    /// </summary>
    public int Level { get; set; }

    /// <summary>
    /// Current status (Draft, Sent, Failed, Cancelled).
    /// </summary>
    public EReminderStatus Status { get; set; }

    /// <summary>
    /// Invoice due date (denormalized).
    /// </summary>
    public DateTime DueDate { get; set; }

    /// <summary>
    /// Date when this reminder was created.
    /// </summary>
    public DateTime ReminderDate { get; set; }

    /// <summary>
    /// Outstanding invoice amount.
    /// </summary>
    public decimal InvoiceAmount { get; set; }

    /// <summary>
    /// Reminder fee in CZK.
    /// </summary>
    public decimal FeeCzk { get; set; }

    /// <summary>
    /// Late payment interest in CZK.
    /// </summary>
    public decimal InterestCzk { get; set; }

    /// <summary>
    /// Total due: InvoiceAmount + FeeCzk + InterestCzk.
    /// </summary>
    public decimal TotalCzk { get; set; }

    /// <summary>
    /// When the email was sent. Null if not sent.
    /// </summary>
    public DateTime? SentAt { get; set; }

    /// <summary>
    /// Email address the reminder was sent to.
    /// </summary>
    public string? SentToEmail { get; set; }

    /// <summary>
    /// Error message if sending failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// User notes (e.g., cancellation reason).
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// When this record was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
