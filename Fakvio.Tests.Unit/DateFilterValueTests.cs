using Fakvio.UI.Shared.Models;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="DateFilterValue.ToRange"/>.
///
/// The method translates the operator + dates picked by the user into an
/// inclusive (From, To) range that maps directly to API parameters like
/// IssueDateFrom / IssueDateTo. Each operator has its own boundary semantics
/// (strict vs inclusive, lower bound only vs upper bound only) which are
/// covered here exhaustively.
/// </summary>
public class DateFilterValueTests
{
    // Fixed reference date — avoids timezone surprises and keeps tests deterministic
    private static readonly DateTime D = new(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc);

    // ─── Empty / not-set ────────────────────────────────────────────────────

    [Fact]
    public void ToRange_NoFromDate_ReturnsBothNull()
    {
        // Filter is empty (user hasn't picked anything yet)
        var v = new DateFilterValue { Operator = "is", From = null, To = null };

        var (from, to) = v.ToRange();

        from.ShouldBeNull();
        to.ShouldBeNull();
    }

    [Fact]
    public void ToRange_NoFromDate_IgnoresToEvenIfSet()
    {
        // Without a primary "From" the filter is meaningless — even a stray "To"
        // value should result in an empty range, not a half-open one.
        var v = new DateFilterValue { Operator = "between", From = null, To = D };

        var (from, to) = v.ToRange();

        from.ShouldBeNull();
        to.ShouldBeNull();
    }

    // ─── "is" — exact day ───────────────────────────────────────────────────

    [Fact]
    public void ToRange_Is_ReturnsExactDay()
    {
        // "= D" → both bounds equal D (single-day range)
        var v = new DateFilterValue { Operator = "is", From = D };

        var (from, to) = v.ToRange();

        from.ShouldBe(D);
        to.ShouldBe(D);
    }

    // ─── "gt" — strictly after ──────────────────────────────────────────────

    [Fact]
    public void ToRange_Gt_ReturnsDayAfterAsLowerBound()
    {
        // "> D" must EXCLUDE D itself → lower bound is D + 1 day, no upper bound
        var v = new DateFilterValue { Operator = "gt", From = D };

        var (from, to) = v.ToRange();

        from.ShouldBe(D.AddDays(1));
        to.ShouldBeNull();
    }

    // ─── "on or after" — >= ─────────────────────────────────────────────────

    [Fact]
    public void ToRange_OnOrAfter_ReturnsDayAsLowerBound()
    {
        // ">= D" includes D → lower bound is D itself, no upper bound
        var v = new DateFilterValue { Operator = "on or after", From = D };

        var (from, to) = v.ToRange();

        from.ShouldBe(D);
        to.ShouldBeNull();
    }

    // ─── "lt" — strictly before ─────────────────────────────────────────────

    [Fact]
    public void ToRange_Lt_ReturnsDayBeforeAsUpperBound()
    {
        // "< D" must EXCLUDE D itself → upper bound is D - 1 day, no lower bound
        var v = new DateFilterValue { Operator = "lt", From = D };

        var (from, to) = v.ToRange();

        from.ShouldBeNull();
        to.ShouldBe(D.AddDays(-1));
    }

    // ─── "on or before" — <= ────────────────────────────────────────────────

    [Fact]
    public void ToRange_OnOrBefore_ReturnsDayAsUpperBound()
    {
        // "<= D" includes D → upper bound is D itself, no lower bound
        var v = new DateFilterValue { Operator = "on or before", From = D };

        var (from, to) = v.ToRange();

        from.ShouldBeNull();
        to.ShouldBe(D);
    }

    // ─── "between" — full range ─────────────────────────────────────────────

    [Fact]
    public void ToRange_Between_WithBothDates_ReturnsInclusiveRange()
    {
        // [D, D + 7] inclusive
        var v = new DateFilterValue
        {
            Operator = "between",
            From = D,
            To = D.AddDays(7)
        };

        var (from, to) = v.ToRange();

        from.ShouldBe(D);
        to.ShouldBe(D.AddDays(7));
    }

    [Fact]
    public void ToRange_Between_WithOnlyFrom_ReturnsHalfOpenRange()
    {
        // User picked "between" but only set the lower bound — treat as ">= From"
        // (the upper bound stays null so the API doesn't impose an arbitrary cap).
        var v = new DateFilterValue { Operator = "between", From = D, To = null };

        var (from, to) = v.ToRange();

        from.ShouldBe(D);
        to.ShouldBeNull();
    }

    // ─── Time-of-day stripping ──────────────────────────────────────────────

    [Fact]
    public void ToRange_StripsTimeFromFromDate()
    {
        // The picker may emit a DateTime with non-zero time (e.g. local "now").
        // ToRange() should normalise to midnight so the SQL comparison is day-aligned.
        var withTime = new DateTime(2026, 4, 10, 14, 35, 22, DateTimeKind.Utc);
        var v = new DateFilterValue { Operator = "is", From = withTime };

        var (from, _) = v.ToRange();

        from!.Value.TimeOfDay.ShouldBe(TimeSpan.Zero);
        from.Value.Date.ShouldBe(new DateTime(2026, 4, 10));
    }

    [Fact]
    public void ToRange_StripsTimeFromToDate()
    {
        var fromWithTime = new DateTime(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc);
        var toWithTime = new DateTime(2026, 4, 10, 17, 30, 0, DateTimeKind.Utc);
        var v = new DateFilterValue
        {
            Operator = "between",
            From = fromWithTime,
            To = toWithTime
        };

        var (from, to) = v.ToRange();

        from!.Value.TimeOfDay.ShouldBe(TimeSpan.Zero);
        to!.Value.TimeOfDay.ShouldBe(TimeSpan.Zero);
        to.Value.Date.ShouldBe(new DateTime(2026, 4, 10));
    }

    // ─── Unknown operator — graceful fallback ───────────────────────────────

    [Fact]
    public void ToRange_UnknownOperator_FallsBackToExactDay()
    {
        // If an unrecognised operator string sneaks in we don't crash —
        // the safest fallback is "= From" so the filter still narrows correctly.
        var v = new DateFilterValue { Operator = "garbage", From = D };

        var (from, to) = v.ToRange();

        from.ShouldBe(D);
        to.ShouldBe(D);
    }

    // ─── Year boundary edge case — verifies AddDays handles month/year rollover ─

    [Fact]
    public void ToRange_Gt_OnNewYearsEve_RollsIntoNextYear()
    {
        var newYearsEve = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var v = new DateFilterValue { Operator = "gt", From = newYearsEve };

        var (from, _) = v.ToRange();

        from.ShouldBe(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ToRange_Lt_OnNewYearsDay_RollsBackToPreviousYear()
    {
        var newYearsDay = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var v = new DateFilterValue { Operator = "lt", From = newYearsDay };

        var (_, to) = v.ToRange();

        to.ShouldBe(new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc));
    }
}
