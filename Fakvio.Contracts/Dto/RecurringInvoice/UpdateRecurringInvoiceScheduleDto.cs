using System.ComponentModel.DataAnnotations;
using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.RecurringInvoice;

/// <summary>
/// DTO for updating an existing recurring invoice schedule.
/// All fields are optional — null means "don't change" (same convention as UpdateClientDto).
/// Changing Frequency/IntervalCount/DayOfMonth/DayOfWeek does NOT recompute NextRunAt —
/// it only takes effect from the next successful run onward.
/// </summary>
public class UpdateRecurringInvoiceScheduleDto
{
    /// <summary>New client, if changed. Null = keep current.</summary>
    public long? ClientId { get; set; }

    /// <summary>New frequency, if changed. Null = keep current.</summary>
    public ERecurrenceFrequency? Frequency { get; set; }

    /// <summary>New interval count, if changed. Null = keep current.</summary>
    [Range(1, 100)]
    public int? IntervalCount { get; set; }

    /// <summary>
    /// New day of month, if changed. Use -1 to explicitly clear it (switch to Weekly).
    /// Null = keep current.
    /// </summary>
    [Range(-1, 28)]
    public int? DayOfMonth { get; set; }

    /// <summary>
    /// New day of week, if changed. Use <see cref="DayOfWeek.Sunday"/> combined with
    /// <see cref="ClearDayOfWeek"/> to clear it (switch away from Weekly). Null = keep current.
    /// </summary>
    public DayOfWeek? DayOfWeek { get; set; }

    /// <summary>Set true to explicitly null out DayOfWeek (e.g. switching from Weekly to Monthly).</summary>
    public bool ClearDayOfWeek { get; set; }

    /// <summary>New end date, if changed. Set <see cref="ClearEndDate"/> to remove the current one.</summary>
    public DateTimeOffset? EndDate { get; set; }

    /// <summary>Set true to explicitly clear EndDate (schedule runs indefinitely).</summary>
    public bool ClearEndDate { get; set; }

    /// <summary>New max occurrences, if changed. Set <see cref="ClearMaxOccurrences"/> to remove the current one.</summary>
    [Range(1, int.MaxValue)]
    public int? MaxOccurrences { get; set; }

    /// <summary>Set true to explicitly clear MaxOccurrences (unlimited occurrences).</summary>
    public bool ClearMaxOccurrences { get; set; }

    /// <summary>New auto-send flag, if changed. Null = keep current.</summary>
    public bool? AutoSend { get; set; }

    /// <summary>Switch period shifting in texts on/off. Null = keep current. Switching ON makes the current template text the period of the NEXT generated invoice.</summary>
    public bool? ShiftPeriodsInText { get; set; }

    /// <summary>Explicit reschedule of the next run. Null = keep current NextRunAt.</summary>
    public DateTimeOffset? NextRunAt { get; set; }
}
