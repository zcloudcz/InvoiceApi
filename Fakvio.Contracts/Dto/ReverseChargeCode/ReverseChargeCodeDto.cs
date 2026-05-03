namespace Fakvio.Contracts.Dto.ReverseChargeCode;

/// <summary>
/// DTO for a reverse charge code (kód předmětu plnění PDP).
/// Read-only transfer object — no create/update DTOs in this task.
/// </summary>
public class ReverseChargeCodeDto
{
    /// <summary>
    /// Database primary key.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Official MFČR code string (e.g., "1", "1a", "5", "21").
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Czech name of the supply type.
    /// </summary>
    public string NameCs { get; set; } = string.Empty;

    /// <summary>
    /// English name of the supply type (optional).
    /// </summary>
    public string? NameEn { get; set; }

    /// <summary>
    /// Paragraph reference in ZDPH (e.g., "§92b", "§92d").
    /// </summary>
    public string ParagraphRef { get; set; } = string.Empty;

    /// <summary>
    /// Date from which this code is valid.
    /// </summary>
    public DateOnly ValidFrom { get; set; }

    /// <summary>
    /// Date until which this code is valid (inclusive). Null = valid indefinitely.
    /// </summary>
    public DateOnly? ValidTo { get; set; }

    /// <summary>
    /// Whether this code is currently active.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Timestamp when this record was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Timestamp of the last update (null if never updated).
    /// </summary>
    public DateTime? UpdatedAt { get; set; }
}
