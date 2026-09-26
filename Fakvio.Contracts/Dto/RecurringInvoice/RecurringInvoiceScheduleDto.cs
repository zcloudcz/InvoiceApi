using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.RecurringInvoice;

/// <summary>
/// Read DTO for a recurring invoice schedule.
/// Contains denormalized template/client names so the UI grid doesn't need extra lookups.
/// </summary>
public class RecurringInvoiceScheduleDto
{
    /// <summary>Primary key of the schedule.</summary>
    public long Id { get; set; }

    /// <summary>FK to the invoice template used to generate each invoice.</summary>
    public long TemplateId { get; set; }

    /// <summary>Template name (denormalized for display).</summary>
    public string? TemplateName { get; set; }

    /// <summary>FK to the client who receives each generated invoice.</summary>
    public long ClientId { get; set; }

    /// <summary>Client company name (denormalized for display).</summary>
    public string? ClientName { get; set; }

    /// <summary>How often the invoice repeats.</summary>
    public ERecurrenceFrequency Frequency { get; set; }

    /// <summary>Multiplier for the frequency (e.g. IntervalCount=2 with Weekly = bi-weekly).</summary>
    public int IntervalCount { get; set; }

    /// <summary>Day of month (1-28) the invoice fires on. Null when Frequency = Weekly.</summary>
    public int? DayOfMonth { get; set; }

    /// <summary>Day of week the invoice fires on. Only used when Frequency = Weekly.</summary>
    public DayOfWeek? DayOfWeek { get; set; }

    /// <summary>UTC timestamp of the next planned invoice generation.</summary>
    public DateTimeOffset NextRunAt { get; set; }

    /// <summary>UTC timestamp of the most recent successful generation. Null if never run.</summary>
    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>Optional hard stop date. Null means "runs indefinitely".</summary>
    public DateTimeOffset? EndDate { get; set; }

    /// <summary>Optional cap on the total number of invoices this schedule may produce.</summary>
    public int? MaxOccurrences { get; set; }

    /// <summary>Running count of invoices generated so far.</summary>
    public int OccurrenceCount { get; set; }

    /// <summary>Whether the schedule is active (considered by the recurring worker).</summary>
    public bool IsActive { get; set; }

    /// <summary>Whether generated invoices are automatically completed and e-mailed to the client.</summary>
    public bool AutoSend { get; set; }

    /// <summary>Message from the last failed generation attempt. Null when the last run succeeded.</summary>
    public string? LastError { get; set; }
}
