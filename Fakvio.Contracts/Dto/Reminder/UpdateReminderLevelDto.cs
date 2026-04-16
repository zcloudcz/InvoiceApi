namespace Fakvio.Contracts.Dto.Reminder;

/// <summary>
/// Write DTO for creating or updating an escalation level within ReminderSettings.
/// Used inside UpdateReminderSettingsDto.Levels collection.
/// </summary>
public class UpdateReminderLevelDto
{
    /// <summary>
    /// Existing level ID for updates. Null or 0 = create new level.
    /// </summary>
    public long? Id { get; set; }

    /// <summary>
    /// Ordinal level number (1, 2, 3, ...). Required.
    /// </summary>
    public int Level { get; set; }

    /// <summary>
    /// Days to wait after the previous level. Required.
    /// </summary>
    public int DaysAfterPrevious { get; set; } = 7;

    /// <summary>
    /// Custom email subject. Null = use template default.
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>
    /// Fixed fee in CZK. Default: 0.
    /// </summary>
    public decimal FixedFeeCzk { get; set; } = 0;

    /// <summary>
    /// FK to email template. Null = use default.
    /// </summary>
    public long? EmailTemplateId { get; set; }

    /// <summary>
    /// FK to PDF template. Null = use default.
    /// </summary>
    public long? PdfTemplateId { get; set; }
}
