namespace Fakvio.Domain.Enums;

/// <summary>
/// Defines how often a recurring invoice schedule repeats.
/// Used by <see cref="Fakvio.Domain.Entities.RecurringInvoiceSchedule"/> to calculate NextRunAt.
///
/// NOTE: The numeric values are stored as integers in PostgreSQL.
/// Do NOT reorder or renumber — existing DB rows reference these values.
/// </summary>
public enum ERecurrenceFrequency
{
    /// <summary>
    /// Invoice is generated once per week (every 7 days from the anchor day).
    /// </summary>
    Weekly = 1,

    /// <summary>
    /// Invoice is generated once per calendar month.
    /// DayOfMonth (on the schedule) controls which day of the month the invoice fires.
    /// </summary>
    Monthly = 2,

    /// <summary>
    /// Invoice is generated once per quarter (every 3 months).
    /// DayOfMonth controls which day of the first month of each quarter.
    /// </summary>
    Quarterly = 3,

    /// <summary>
    /// Invoice is generated once per year (on the same month/day every year).
    /// DayOfMonth controls the day; the month is derived from the initial NextRunAt.
    /// </summary>
    Yearly = 4,
}
