using Shouldly;
using InvoiceApi.Contracts.Common;
using InvoiceApi.Domain.Enums;

namespace InvoiceApi.Tests.Unit;

/// <summary>
/// Unit tests for DueDateCalculator — the shared helper that calculates invoice due dates
/// based on EDueDateCalculationType. These tests verify all 4 calculation algorithms
/// and ensure consistent behavior for edge cases (leap years, end-of-month, year boundaries).
/// </summary>
public class DueDateCalculatorTests
{
    // ======================= DaysFromIssue =======================

    [Fact]
    public void DaysFromIssue_AddsExactDaysToIssueDate()
    {
        // Arrange: Jan 15 + 14 days = Jan 29
        var issueDate = new DateTime(2026, 1, 15);

        // Act
        var result = DueDateCalculator.Calculate(issueDate, dueDays: 14, EDueDateCalculationType.DaysFromIssue);

        // Assert
        result.ShouldBe(new DateTime(2026, 1, 29));
    }

    [Fact]
    public void DaysFromIssue_CrossesMonthBoundary()
    {
        // Arrange: Jan 25 + 14 days = Feb 8
        var issueDate = new DateTime(2026, 1, 25);

        // Act
        var result = DueDateCalculator.Calculate(issueDate, dueDays: 14, EDueDateCalculationType.DaysFromIssue);

        // Assert
        result.ShouldBe(new DateTime(2026, 2, 8));
    }

    [Fact]
    public void DaysFromIssue_CrossesYearBoundary()
    {
        // Arrange: Dec 25 + 14 days = Jan 8 of next year
        var issueDate = new DateTime(2026, 12, 25);

        // Act
        var result = DueDateCalculator.Calculate(issueDate, dueDays: 14, EDueDateCalculationType.DaysFromIssue);

        // Assert
        result.ShouldBe(new DateTime(2027, 1, 8));
    }

    [Fact]
    public void DaysFromIssue_ZeroDays_ReturnsSameDate()
    {
        // Arrange: 0 days means due immediately
        var issueDate = new DateTime(2026, 3, 10);

        // Act
        var result = DueDateCalculator.Calculate(issueDate, dueDays: 0, EDueDateCalculationType.DaysFromIssue);

        // Assert
        result.ShouldBe(new DateTime(2026, 3, 10));
    }

    // ======================= DaysFromEndOfMonth =======================

    [Fact]
    public void DaysFromEndOfMonth_AddsFromLastDayOfIssueMonth()
    {
        // Arrange: Jan 15, end of Jan = Jan 31, + 14 days = Feb 14
        var issueDate = new DateTime(2026, 1, 15);

        // Act
        var result = DueDateCalculator.Calculate(issueDate, dueDays: 14, EDueDateCalculationType.DaysFromEndOfMonth);

        // Assert
        result.ShouldBe(new DateTime(2026, 2, 14));
    }

    [Fact]
    public void DaysFromEndOfMonth_FebruaryNonLeapYear()
    {
        // Arrange: Feb 10 2026, end of Feb = Feb 28, + 14 days = Mar 14
        var issueDate = new DateTime(2026, 2, 10);

        // Act
        var result = DueDateCalculator.Calculate(issueDate, dueDays: 14, EDueDateCalculationType.DaysFromEndOfMonth);

        // Assert
        result.ShouldBe(new DateTime(2026, 3, 14));
    }

    [Fact]
    public void DaysFromEndOfMonth_FebruaryLeapYear()
    {
        // Arrange: Feb 10 2028 (leap year), end of Feb = Feb 29, + 14 days = Mar 14
        var issueDate = new DateTime(2028, 2, 10);

        // Act
        var result = DueDateCalculator.Calculate(issueDate, dueDays: 14, EDueDateCalculationType.DaysFromEndOfMonth);

        // Assert
        result.ShouldBe(new DateTime(2028, 3, 14));
    }

    [Fact]
    public void DaysFromEndOfMonth_IssuedOnLastDay_StillAddsFromEndOfMonth()
    {
        // Arrange: Jan 31 (already last day), end of Jan = Jan 31, + 14 = Feb 14
        var issueDate = new DateTime(2026, 1, 31);

        // Act
        var result = DueDateCalculator.Calculate(issueDate, dueDays: 14, EDueDateCalculationType.DaysFromEndOfMonth);

        // Assert
        result.ShouldBe(new DateTime(2026, 2, 14));
    }

    [Fact]
    public void DaysFromEndOfMonth_ZeroDays_ReturnsEndOfMonth()
    {
        // Arrange: Jan 15, + 0 days from end of month = Jan 31
        var issueDate = new DateTime(2026, 1, 15);

        // Act
        var result = DueDateCalculator.Calculate(issueDate, dueDays: 0, EDueDateCalculationType.DaysFromEndOfMonth);

        // Assert
        result.ShouldBe(new DateTime(2026, 1, 31));
    }

    // ======================= EndOfNextMonth =======================

    [Fact]
    public void EndOfNextMonth_ReturnsLastDayOfFollowingMonth()
    {
        // Arrange: Jan 15 → end of February
        var issueDate = new DateTime(2026, 1, 15);

        // Act
        var result = DueDateCalculator.Calculate(issueDate, dueDays: 14, EDueDateCalculationType.EndOfNextMonth);

        // Assert: Feb 2026 has 28 days (not a leap year)
        result.ShouldBe(new DateTime(2026, 2, 28));
    }

    [Fact]
    public void EndOfNextMonth_IgnoresDueDays()
    {
        // Arrange: DueDays is irrelevant for EndOfNextMonth
        var issueDate = new DateTime(2026, 1, 15);

        // Act — different dueDays values should give the same result
        var result1 = DueDateCalculator.Calculate(issueDate, dueDays: 0, EDueDateCalculationType.EndOfNextMonth);
        var result2 = DueDateCalculator.Calculate(issueDate, dueDays: 30, EDueDateCalculationType.EndOfNextMonth);
        var result3 = DueDateCalculator.Calculate(issueDate, dueDays: 100, EDueDateCalculationType.EndOfNextMonth);

        // Assert: all return end of February regardless of dueDays
        result1.ShouldBe(new DateTime(2026, 2, 28));
        result2.ShouldBe(new DateTime(2026, 2, 28));
        result3.ShouldBe(new DateTime(2026, 2, 28));
    }

    [Fact]
    public void EndOfNextMonth_DecemberWrapsToJanuaryOfNextYear()
    {
        // Arrange: Dec 10 → end of January next year
        var issueDate = new DateTime(2026, 12, 10);

        // Act
        var result = DueDateCalculator.Calculate(issueDate, dueDays: 14, EDueDateCalculationType.EndOfNextMonth);

        // Assert
        result.ShouldBe(new DateTime(2027, 1, 31));
    }

    [Fact]
    public void EndOfNextMonth_JanuaryLeapYear()
    {
        // Arrange: Jan 2028 → end of Feb 2028 (leap year, 29 days)
        var issueDate = new DateTime(2028, 1, 15);

        // Act
        var result = DueDateCalculator.Calculate(issueDate, dueDays: 0, EDueDateCalculationType.EndOfNextMonth);

        // Assert
        result.ShouldBe(new DateTime(2028, 2, 29));
    }

    // ======================= EndOfCurrentMonth =======================

    [Fact]
    public void EndOfCurrentMonth_ReturnsLastDayOfIssueMonth()
    {
        // Arrange: Jan 15 → Jan 31
        var issueDate = new DateTime(2026, 1, 15);

        // Act
        var result = DueDateCalculator.Calculate(issueDate, dueDays: 14, EDueDateCalculationType.EndOfCurrentMonth);

        // Assert
        result.ShouldBe(new DateTime(2026, 1, 31));
    }

    [Fact]
    public void EndOfCurrentMonth_IgnoresDueDays()
    {
        // Arrange: DueDays is irrelevant for EndOfCurrentMonth
        var issueDate = new DateTime(2026, 3, 10);

        // Act
        var result1 = DueDateCalculator.Calculate(issueDate, dueDays: 0, EDueDateCalculationType.EndOfCurrentMonth);
        var result2 = DueDateCalculator.Calculate(issueDate, dueDays: 30, EDueDateCalculationType.EndOfCurrentMonth);

        // Assert: Mar 31 regardless of dueDays
        result1.ShouldBe(new DateTime(2026, 3, 31));
        result2.ShouldBe(new DateTime(2026, 3, 31));
    }

    [Fact]
    public void EndOfCurrentMonth_FebruaryNonLeapYear()
    {
        // Arrange: Feb 10 2026 → Feb 28
        var issueDate = new DateTime(2026, 2, 10);

        // Act
        var result = DueDateCalculator.Calculate(issueDate, dueDays: 0, EDueDateCalculationType.EndOfCurrentMonth);

        // Assert
        result.ShouldBe(new DateTime(2026, 2, 28));
    }

    [Fact]
    public void EndOfCurrentMonth_FebruaryLeapYear()
    {
        // Arrange: Feb 10 2028 (leap year) → Feb 29
        var issueDate = new DateTime(2028, 2, 10);

        // Act
        var result = DueDateCalculator.Calculate(issueDate, dueDays: 0, EDueDateCalculationType.EndOfCurrentMonth);

        // Assert
        result.ShouldBe(new DateTime(2028, 2, 29));
    }

    [Fact]
    public void EndOfCurrentMonth_AlreadyOnLastDay()
    {
        // Arrange: Jan 31 → still Jan 31
        var issueDate = new DateTime(2026, 1, 31);

        // Act
        var result = DueDateCalculator.Calculate(issueDate, dueDays: 0, EDueDateCalculationType.EndOfCurrentMonth);

        // Assert
        result.ShouldBe(new DateTime(2026, 1, 31));
    }

    // ======================= April (30-day month) edge cases =======================

    [Fact]
    public void DaysFromEndOfMonth_30DayMonth()
    {
        // Arrange: Apr 10, end of Apr = Apr 30, + 14 = May 14
        var issueDate = new DateTime(2026, 4, 10);

        // Act
        var result = DueDateCalculator.Calculate(issueDate, dueDays: 14, EDueDateCalculationType.DaysFromEndOfMonth);

        // Assert
        result.ShouldBe(new DateTime(2026, 5, 14));
    }

    [Fact]
    public void EndOfCurrentMonth_30DayMonth()
    {
        // Arrange: Apr 10 → Apr 30
        var issueDate = new DateTime(2026, 4, 10);

        // Act
        var result = DueDateCalculator.Calculate(issueDate, dueDays: 0, EDueDateCalculationType.EndOfCurrentMonth);

        // Assert
        result.ShouldBe(new DateTime(2026, 4, 30));
    }
}
