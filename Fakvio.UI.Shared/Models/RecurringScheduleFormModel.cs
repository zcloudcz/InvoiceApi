using Fakvio.Domain.Enums;

namespace Fakvio.UI.Shared.Models;

/// <summary>
/// Local edit model for <c>RecurringScheduleEditor.razor</c>. Bridges the "end date OR max
/// occurrences" UI toggle (<see cref="EndMode"/>) with the two independent nullable API fields —
/// keeps the component free of any API/DTO knowledge (dumb, reusable form).
/// </summary>
public class RecurringScheduleFormModel
{
    public long? Id { get; set; }
    public long TemplateId { get; set; }
    public long ClientId { get; set; }
    public ERecurrenceFrequency Frequency { get; set; } = ERecurrenceFrequency.Monthly;
    public int IntervalCount { get; set; } = 1;
    public int? DayOfMonthValue { get; set; } = 1;
    public DayOfWeek? DayOfWeekValue { get; set; }
    public DateTime? StartDate { get; set; } = DateTime.Today;

    /// <summary>"none" | "date" | "count" — which of EndDate/MaxOccurrences is active, if any.</summary>
    public string EndMode { get; set; } = "none";
    public DateTime? EndDate { get; set; }
    public int? MaxOccurrences { get; set; }

    public bool AutoSend { get; set; }
}
