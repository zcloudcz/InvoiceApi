namespace Fakvio.Contracts.Dto.Reminder;

/// <summary>
/// Write DTO for creating or updating reminder settings (upsert).
/// If ClientId is null, updates/creates company-wide defaults.
/// If ClientId is set, updates/creates a per-client override.
/// </summary>
public class UpdateReminderSettingsDto
{
    /// <summary>
    /// FK to Client. Null = company default, non-null = per-client override.
    /// </summary>
    public long? ClientId { get; set; }

    /// <summary>
    /// Master switch for enabling/disabling reminders.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Maximum escalation levels (1–5).
    /// </summary>
    public int MaxReminderLevel { get; set; } = 3;

    /// <summary>
    /// Grace period in days before the first reminder.
    /// </summary>
    public int GracePeriodDays { get; set; } = 7;

    /// <summary>
    /// Include statutory late payment interest.
    /// </summary>
    public bool IncludeInterest { get; set; } = false;

    /// <summary>
    /// Attach invoice PDF to reminder emails.
    /// </summary>
    public bool AttachInvoicePdf { get; set; } = true;

    /// <summary>
    /// Auto-send emails or create as Draft for manual review.
    /// </summary>
    public bool AutoSendEmail { get; set; } = true;

    /// <summary>
    /// Escalation levels. Replaces all existing levels on save.
    /// </summary>
    public List<UpdateReminderLevelDto> Levels { get; set; } = new();
}
