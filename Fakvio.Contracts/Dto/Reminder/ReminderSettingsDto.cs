namespace Fakvio.Contracts.Dto.Reminder;

/// <summary>
/// Read DTO for reminder settings — includes all levels.
/// Returned by GET /api/reminder/settings endpoints.
/// </summary>
public class ReminderSettingsDto
{
    /// <summary>
    /// Primary key of the settings record.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// FK to Client. Null = company-wide default settings.
    /// </summary>
    public long? ClientId { get; set; }

    /// <summary>
    /// Client company name (for display). Null for company default.
    /// </summary>
    public string? ClientName { get; set; }

    /// <summary>
    /// Master switch — false disables all reminders for this scope.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Maximum escalation levels (1–5).
    /// </summary>
    public int MaxReminderLevel { get; set; }

    /// <summary>
    /// Days after due date before the first reminder.
    /// </summary>
    public int GracePeriodDays { get; set; }

    /// <summary>
    /// Whether to include statutory late payment interest calculation.
    /// </summary>
    public bool IncludeInterest { get; set; }

    /// <summary>
    /// Whether to attach the invoice PDF to reminder emails.
    /// </summary>
    public bool AttachInvoicePdf { get; set; }

    /// <summary>
    /// Whether to auto-send emails (true) or create Draft reminders (false).
    /// </summary>
    public bool AutoSendEmail { get; set; }

    /// <summary>
    /// Escalation level definitions ordered by Level.
    /// </summary>
    public List<ReminderLevelDto> Levels { get; set; } = new();
}
