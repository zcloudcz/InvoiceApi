namespace Fakvio.Contracts.Dto.Reminder;

/// <summary>
/// Read DTO for a single escalation level within ReminderSettings.
/// Defines when to send, what fee to apply, and which templates to use.
/// </summary>
public class ReminderLevelDto
{
    /// <summary>
    /// Primary key of this level record.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Ordinal level number (1, 2, 3, ...).
    /// </summary>
    public int Level { get; set; }

    /// <summary>
    /// Days to wait after the previous level (or after grace period for level 1).
    /// </summary>
    public int DaysAfterPrevious { get; set; }

    /// <summary>
    /// Custom email subject for this level. Null = use template default.
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>
    /// Fixed fee in CZK charged for this reminder level.
    /// </summary>
    public decimal FixedFeeCzk { get; set; }

    /// <summary>
    /// FK to the email template (ContentTemplate with type ReminderEmail).
    /// Null = use default template.
    /// </summary>
    public long? EmailTemplateId { get; set; }

    /// <summary>
    /// FK to the PDF template (ContentTemplate with type ReminderPdf).
    /// Null = use default template.
    /// </summary>
    public long? PdfTemplateId { get; set; }
}
