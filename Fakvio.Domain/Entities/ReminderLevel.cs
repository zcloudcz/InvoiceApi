using Fakvio.Domain.Common;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Defines one escalation level in the reminder (dunning) sequence.
/// Each level specifies when to send, what fee to charge, and which template to use.
///
/// Example 3-level setup:
///   Level 1: +7 days after due date, no fee, friendly reminder template
///   Level 2: +14 days after level 1, 50 CZK fee, formal notice template
///   Level 3: +14 days after level 2, 200 CZK fee, final warning template
/// </summary>
public class ReminderLevel : BaseEntity
{
    /// <summary>
    /// FK to the parent ReminderSettings record.
    /// </summary>
    public long ReminderSettingsId { get; set; }

    /// <summary>
    /// Navigation property to parent settings.
    /// </summary>
    public ReminderSettings ReminderSettings { get; set; } = null!;

    /// <summary>
    /// Ordinal position of this level in the escalation sequence (1, 2, 3, ...).
    /// Level 1 is the first reminder, level 2 the second, etc.
    /// Must be unique within a ReminderSettings record.
    /// </summary>
    public int Level { get; set; }

    /// <summary>
    /// Days to wait after the previous level (or after GracePeriodDays for level 1).
    /// For level 1: effective day = DueDate + GracePeriodDays.
    /// For level N: effective day = previous reminder date + DaysAfterPrevious.
    /// Default: 7 days.
    /// </summary>
    public int DaysAfterPrevious { get; set; } = 7;

    /// <summary>
    /// Custom email subject line for this level. Null = use template's default subject.
    /// Allows escalating tone in subject, e.g.:
    ///   Level 1: "Payment Reminder — Invoice {{InvoiceNumber}}"
    ///   Level 3: "FINAL WARNING — Invoice {{InvoiceNumber}}"
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>
    /// Fixed fee charged for this reminder level, in CZK.
    /// Added to the total due amount on the reminder.
    /// Default: 0 (no fee for first reminders, increasing for later levels).
    /// </summary>
    public decimal FixedFeeCzk { get; set; } = 0;

    /// <summary>
    /// FK to ContentTemplate (type = ReminderEmail) for this level's email body.
    /// Null = use the default ReminderEmail template.
    /// </summary>
    public long? EmailTemplateId { get; set; }

    /// <summary>
    /// Navigation property to the email template.
    /// </summary>
    public ContentTemplate? EmailTemplate { get; set; }

    /// <summary>
    /// FK to ContentTemplate (type = ReminderPdf) for this level's PDF letter.
    /// Null = use the default ReminderPdf template (or skip PDF generation).
    /// </summary>
    public long? PdfTemplateId { get; set; }

    /// <summary>
    /// Navigation property to the PDF template.
    /// </summary>
    public ContentTemplate? PdfTemplate { get; set; }
}
