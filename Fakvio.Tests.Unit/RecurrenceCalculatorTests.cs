using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for the pure NextRunAt calculation. Covers the calendar edge cases the story
/// explicitly calls out: leap-year February, year-end quarter rollover, and multi-period
/// intervals for both Monthly and Weekly frequencies.
/// </summary>
public class RecurrenceCalculatorTests
{
    [Theory]
    [InlineData(ERecurrenceFrequency.Monthly, 1, 0, 1)]
    [InlineData(ERecurrenceFrequency.Monthly, 2, 2, 6)]
    [InlineData(ERecurrenceFrequency.Quarterly, 1, 0, 3)]
    [InlineData(ERecurrenceFrequency.Yearly, 1, 1, 24)]
    [InlineData(ERecurrenceFrequency.Weekly, 1, 5, 0)]
    public void PeriodShiftMonths_ScalesWithIntervalAndOccurrence(ERecurrenceFrequency f, int interval, int done, int expected) =>
        RecurrenceCalculator.PeriodShiftMonths(f, interval, done).ShouldBe(expected);

    [Fact]
    public void Next_Monthly_AdvancesOneMonthOnSameDay()
    {
        var from = new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);

        var next = RecurrenceCalculator.Next(from, ERecurrenceFrequency.Monthly, intervalCount: 1, dayOfMonth: 15, dayOfWeek: null);

        next.ShouldBe(new DateTimeOffset(2026, 2, 15, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Next_Monthly_OverLeapFebruary_LandsOnDay28()
    {
        // 2028 is a leap year — day 28 exists in both January and February either way,
        // this exercises the "always valid because capped at 28" invariant.
        var from = new DateTimeOffset(2028, 1, 28, 0, 0, 0, TimeSpan.Zero);

        var next = RecurrenceCalculator.Next(from, ERecurrenceFrequency.Monthly, intervalCount: 1, dayOfMonth: 28, dayOfWeek: null);

        next.ShouldBe(new DateTimeOffset(2028, 2, 28, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Next_Quarterly_OverYearEnd_RollsIntoNextYear()
    {
        var from = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);

        var next = RecurrenceCalculator.Next(from, ERecurrenceFrequency.Quarterly, intervalCount: 1, dayOfMonth: 10, dayOfWeek: null);

        next.ShouldBe(new DateTimeOffset(2027, 2, 10, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Next_Yearly_AdvancesTwelveMonths()
    {
        var from = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

        var next = RecurrenceCalculator.Next(from, ERecurrenceFrequency.Yearly, intervalCount: 1, dayOfMonth: 1, dayOfWeek: null);

        next.ShouldBe(new DateTimeOffset(2027, 3, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Next_Monthly_IntervalTwo_SkipsAMonth()
    {
        var from = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

        var next = RecurrenceCalculator.Next(from, ERecurrenceFrequency.Monthly, intervalCount: 2, dayOfMonth: 5, dayOfWeek: null);

        next.ShouldBe(new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Next_Weekly_AdvancesToSameDayOfWeekNextWeek()
    {
        // 2026-09-10 is a Thursday.
        var from = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);

        var next = RecurrenceCalculator.Next(from, ERecurrenceFrequency.Weekly, intervalCount: 1, dayOfMonth: null, dayOfWeek: DayOfWeek.Thursday);

        next.ShouldBe(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Next_Weekly_IntervalTwo_SkipsAWeek()
    {
        var from = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero); // Thursday

        var next = RecurrenceCalculator.Next(from, ERecurrenceFrequency.Weekly, intervalCount: 2, dayOfMonth: null, dayOfWeek: DayOfWeek.Thursday);

        next.ShouldBe(new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Next_Weekly_DifferentAnchorDay_SnapsForward()
    {
        // From a Thursday, targeting Monday — should land on the Monday of the following week
        // (7 * 1 days ahead, then snapped forward to the next Monday).
        var from = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero); // Thursday

        var next = RecurrenceCalculator.Next(from, ERecurrenceFrequency.Weekly, intervalCount: 1, dayOfMonth: null, dayOfWeek: DayOfWeek.Monday);

        next.ShouldBe(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero)); // next Monday
    }

    [Fact]
    public void Next_Monthly_MissingDayOfMonth_Throws()
    {
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Should.Throw<ArgumentOutOfRangeException>(() =>
            RecurrenceCalculator.Next(from, ERecurrenceFrequency.Monthly, intervalCount: 1, dayOfMonth: null, dayOfWeek: null));
    }

    [Fact]
    public void Next_Weekly_MissingDayOfWeek_Throws()
    {
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Should.Throw<ArgumentNullException>(() =>
            RecurrenceCalculator.Next(from, ERecurrenceFrequency.Weekly, intervalCount: 1, dayOfMonth: null, dayOfWeek: null));
    }
}
