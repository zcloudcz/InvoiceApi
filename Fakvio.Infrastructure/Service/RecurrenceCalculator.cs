using Fakvio.Domain.Enums;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Pure calculation of the next fire date for a recurring invoice schedule. No DB access,
/// no side effects — easy to unit test exhaustively for calendar edge cases (leap years,
/// month-end clamping, year boundaries).
///
/// Day-of-month handling: schedules only allow DayOfMonth 1-28 (enforced by the DTO/service
/// validation) specifically so every month has that day — no "clamp to last day" logic needed.
/// </summary>
internal static class RecurrenceCalculator
{
    /// <summary>
    /// Months by which period text in the template ("Hosting 3/2026") moves for a generated invoice, given how many
    /// runs happened since the invoice the template text describes (0 = that invoice itself → no shift).
    /// Weekly schedules return 0 (a week never changes a month period).
    /// </summary>
    public static int PeriodShiftMonths(ERecurrenceFrequency frequency, int intervalCount, int runsSinceBaseline)
    {
        var monthsPerInterval = frequency switch
        {
            ERecurrenceFrequency.Monthly => 1,
            ERecurrenceFrequency.Quarterly => 3,
            ERecurrenceFrequency.Yearly => 12,
            _ => 0,
        };
        return monthsPerInterval * intervalCount * runsSinceBaseline;
    }

    /// <summary>
    /// Computes the next occurrence strictly after <paramref name="from"/>.
    /// </summary>
    /// <param name="from">The previous NextRunAt (the occurrence that just fired).</param>
    /// <param name="frequency">Weekly / Monthly / Quarterly / Yearly.</param>
    /// <param name="intervalCount">Multiplier — how many periods to advance (>= 1).</param>
    /// <param name="dayOfMonth">Target day of month (1-28). Required for non-Weekly frequencies.</param>
    /// <param name="dayOfWeek">Target day of week. Required for Weekly frequency.</param>
    public static DateTimeOffset Next(
        DateTimeOffset from,
        ERecurrenceFrequency frequency,
        int intervalCount,
        int? dayOfMonth,
        DayOfWeek? dayOfWeek)
    {
        if (intervalCount < 1)
            throw new ArgumentOutOfRangeException(nameof(intervalCount), "IntervalCount must be >= 1.");

        switch (frequency)
        {
            case ERecurrenceFrequency.Weekly:
            {
                if (dayOfWeek is null)
                    throw new ArgumentNullException(nameof(dayOfWeek), "Weekly schedules require DayOfWeek.");

                // Advance by whole weeks first, then snap onto the target day of week.
                var candidate = from.AddDays(7 * intervalCount);
                var diff = ((int)dayOfWeek.Value - (int)candidate.DayOfWeek + 7) % 7;
                return candidate.AddDays(diff);
            }

            case ERecurrenceFrequency.Monthly:
                return NextByMonth(from, intervalCount, dayOfMonth);

            case ERecurrenceFrequency.Quarterly:
                return NextByMonth(from, intervalCount * 3, dayOfMonth);

            case ERecurrenceFrequency.Yearly:
                return NextByMonth(from, intervalCount * 12, dayOfMonth);

            default:
                throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "Unknown recurrence frequency.");
        }
    }

    /// <summary>
    /// Advances <paramref name="from"/> by <paramref name="monthsToAdd"/> calendar months and
    /// pins the day-of-month to <paramref name="dayOfMonth"/> (1-28, so always valid — no
    /// February 29/30/31 clamping needed).
    /// </summary>
    private static DateTimeOffset NextByMonth(DateTimeOffset from, int monthsToAdd, int? dayOfMonth)
    {
        if (dayOfMonth is null or < 1 or > 28)
            throw new ArgumentOutOfRangeException(nameof(dayOfMonth), "DayOfMonth must be 1-28 for non-Weekly schedules.");

        var advanced = from.AddMonths(monthsToAdd);
        return new DateTimeOffset(
            advanced.Year, advanced.Month, dayOfMonth.Value,
            from.Hour, from.Minute, from.Second, from.Offset);
    }
}
