using System.ComponentModel.DataAnnotations;
using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.RecurringInvoice;

/// <summary>
/// DTO for creating a new recurring invoice schedule on an invoice template.
/// </summary>
public class CreateRecurringInvoiceScheduleDto
{
    /// <summary>FK to the invoice template used to generate each invoice. Required.</summary>
    [Required]
    public long TemplateId { get; set; }

    /// <summary>FK to the client who will receive each generated invoice. Required.</summary>
    [Required]
    public long ClientId { get; set; }

    /// <summary>How often the invoice repeats. Required.</summary>
    public ERecurrenceFrequency Frequency { get; set; }

    /// <summary>Multiplier for the frequency. Must be &gt;= 1. Default 1 (every period).</summary>
    [Range(1, 100)]
    public int IntervalCount { get; set; } = 1;

    /// <summary>
    /// Day of month (1-28) the invoice fires on. Required for Monthly/Quarterly/Yearly,
    /// must be null for Weekly.
    /// </summary>
    [Range(1, 28)]
    public int? DayOfMonth { get; set; }

    /// <summary>
    /// Day of week the invoice fires on. Required for Weekly, must be null otherwise.
    /// </summary>
    public DayOfWeek? DayOfWeek { get; set; }

    /// <summary>
    /// First planned invoice generation date (UTC). Becomes the schedule's initial NextRunAt —
    /// subsequent runs are computed by RecurrenceCalculator from this anchor.
    /// </summary>
    [Required]
    public DateTimeOffset StartDate { get; set; }

    /// <summary>Optional hard stop date — the schedule will not fire on or after this date.</summary>
    public DateTimeOffset? EndDate { get; set; }

    /// <summary>Optional cap on how many invoices this schedule may produce in total.</summary>
    [Range(1, int.MaxValue)]
    public int? MaxOccurrences { get; set; }

    /// <summary>Whether the generated invoice should be automatically completed and e-mailed.</summary>
    public bool AutoSend { get; set; } = false;
}
