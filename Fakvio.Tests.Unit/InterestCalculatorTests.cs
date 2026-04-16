using Fakvio.Infrastructure.Service;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for InterestCalculator — the stateless service that calculates
/// Czech statutory late payment interest per Civil Code § 1970 and Government
/// Regulation 351/2013 Sb.
///
/// Formula: Interest = principal * (rate / 100) * daysOverdue / 365
/// Rate = CNB repo rate (for the half-year when delay started) + 8 percentage points.
///
/// The calculator is a simple stateless class with no dependencies,
/// so we just instantiate it directly with new InterestCalculator().
/// </summary>
public class InterestCalculatorTests
{
    // Shared calculator instance — stateless and thread-safe, safe to reuse across tests.
    private readonly InterestCalculator _calculator = new();

    // ======================= Calculate — positive scenarios =======================

    [Fact]
    public void Calculate_KnownPeriod1H2026_ReturnsCorrectInterest()
    {
        // Arrange: 50,000 CZK overdue by 30 days.
        // Due date 2026-01-01, calculation date 2026-01-31 → 30 days overdue.
        // Delay starts 2026-01-02 (1H 2026): repo 3.50% + 8 = 11.50%.
        // Interest = 50000 * (11.50 / 100) * 30 / 365 = 472.60 (rounded).
        var principal = 50_000m;
        var dueDate = new DateTime(2026, 1, 1);
        var calculationDate = new DateTime(2026, 1, 31);

        // Act
        var result = _calculator.Calculate(principal, dueDate, calculationDate);

        // Assert
        result.ShouldBe(472.60m);
    }

    [Fact]
    public void Calculate_KnownPeriod2H2024_ReturnsCorrectInterest()
    {
        // Arrange: 100,000 CZK overdue by 60 days.
        // Due date 2024-08-01, calculation date 2024-09-30 → 60 days overdue.
        // Delay starts 2024-08-02 (2H 2024): repo 4.75% + 8 = 12.75%.
        // Interest = 100000 * (12.75 / 100) * 60 / 365 = 2095.89 (rounded).
        var principal = 100_000m;
        var dueDate = new DateTime(2024, 8, 1);
        var calculationDate = new DateTime(2024, 9, 30);

        // Act
        var result = _calculator.Calculate(principal, dueDate, calculationDate);

        // Assert
        result.ShouldBe(2095.89m);
    }

    // ======================= Calculate — zero/no-interest scenarios =======================

    [Fact]
    public void Calculate_DueDateInFuture_ReturnsZero()
    {
        // Arrange: calculation date is before the due date — invoice is not overdue yet.
        var principal = 50_000m;
        var dueDate = new DateTime(2026, 6, 1);
        var calculationDate = new DateTime(2026, 5, 15);

        // Act
        var result = _calculator.Calculate(principal, dueDate, calculationDate);

        // Assert: no interest when not yet overdue.
        result.ShouldBe(0m);
    }

    [Fact]
    public void Calculate_DueDateEqualsCalculationDate_ReturnsZero()
    {
        // Arrange: due date == calculation date — technically not overdue yet.
        // The condition is calculationDate <= dueDate → returns 0.
        var principal = 50_000m;
        var sameDate = new DateTime(2026, 3, 15);

        // Act
        var result = _calculator.Calculate(principal, sameDate, sameDate);

        // Assert: no interest on the due date itself.
        result.ShouldBe(0m);
    }

    [Fact]
    public void Calculate_ZeroPrincipal_ReturnsZero()
    {
        // Arrange: zero principal — nothing to charge interest on.
        var principal = 0m;
        var dueDate = new DateTime(2026, 1, 1);
        var calculationDate = new DateTime(2026, 2, 1);

        // Act
        var result = _calculator.Calculate(principal, dueDate, calculationDate);

        // Assert
        result.ShouldBe(0m);
    }

    [Fact]
    public void Calculate_NegativePrincipal_ReturnsZero()
    {
        // Arrange: negative principal — the calculator guards against this.
        var principal = -10_000m;
        var dueDate = new DateTime(2026, 1, 1);
        var calculationDate = new DateTime(2026, 2, 1);

        // Act
        var result = _calculator.Calculate(principal, dueDate, calculationDate);

        // Assert: negative amounts should not produce interest.
        result.ShouldBe(0m);
    }

    // ======================= GetInterestRate =======================

    [Fact]
    public void GetInterestRate_1H2026_Returns11_50()
    {
        // Arrange: any date in Jan–Jun 2026 → 1H 2026, repo 3.50% + 8 = 11.50%.
        var delayStart = new DateTime(2026, 3, 15);

        // Act
        var rate = _calculator.GetInterestRate(delayStart);

        // Assert
        rate.ShouldBe(11.50m);
    }

    [Fact]
    public void GetInterestRate_2H2025_Returns11_75()
    {
        // Arrange: any date in Jul–Dec 2025 → 2H 2025, repo 3.75% + 8 = 11.75%.
        var delayStart = new DateTime(2025, 9, 1);

        // Act
        var rate = _calculator.GetInterestRate(delayStart);

        // Assert
        rate.ShouldBe(11.75m);
    }

    [Fact]
    public void GetInterestRate_FuturePeriod_FallsBackToLastKnown()
    {
        // Arrange: a date far in the future (2H 2028) — not in the lookup table.
        // The calculator should fall back to the last known rate: 1H 2026, repo 3.50%.
        // Interest rate = 3.50 + 8 = 11.50%.
        var delayStart = new DateTime(2028, 10, 1);

        // Act
        var rate = _calculator.GetInterestRate(delayStart);

        // Assert: falls back to the most recent entry (1H 2026).
        rate.ShouldBe(11.50m);
    }

    // ======================= Leap year edge case =======================

    [Fact]
    public void Calculate_LeapYear_CalculatesCorrectly()
    {
        // Arrange: due date 2028-02-28, calculation date 2028-03-29 → 30 days overdue.
        // 2028 is a leap year (Feb has 29 days), so the period crosses Feb 29.
        // Delay starts 2028-02-29 (1H 2028) — future period, falls back to last known repo 3.50%.
        // Rate = 3.50 + 8 = 11.50%.
        // Interest = 50000 * (11.50 / 100) * 30 / 365 = 472.60 (rounded).
        // The formula always divides by 365 regardless of leap year — this is the Czech standard.
        var principal = 50_000m;
        var dueDate = new DateTime(2028, 2, 28);
        var calculationDate = new DateTime(2028, 3, 29);

        // Act
        var result = _calculator.Calculate(principal, dueDate, calculationDate);

        // Assert
        result.ShouldBe(472.60m);
    }
}
