namespace Fakvio.Application.Service;

/// <summary>
/// Calculates statutory late payment interest per Czech Civil Code § 1970.
///
/// The interest rate is determined by:
///   Rate = CNB repo rate (as of the 1st day of the half-year when the delay started) + 8 percentage points
///
/// The interest amount is:
///   Interest = principal × (rate / 100) × daysOverdue / 365
///
/// Example: Invoice 50,000 CZK, 30 days overdue, rate 11.50% → 472.60 CZK
///
/// The repo rate table is hardcoded and must be updated twice a year (Jan 1 and Jul 1).
/// For unknown future periods, the last known rate is used as a fallback.
/// </summary>
public interface IInterestCalculator
{
    /// <summary>
    /// Calculate the late payment interest for a given principal amount.
    /// </summary>
    /// <param name="principal">The outstanding invoice amount (the base for interest calculation).</param>
    /// <param name="dueDate">The original due date of the invoice.</param>
    /// <param name="calculationDate">The date on which to calculate interest (usually today).</param>
    /// <returns>The interest amount in CZK. Returns 0 if dueDate >= calculationDate.</returns>
    decimal Calculate(decimal principal, DateTime dueDate, DateTime calculationDate);

    /// <summary>
    /// Get the applicable interest rate percentage for a given date.
    /// Useful for displaying the rate on reminders.
    /// </summary>
    /// <param name="delayStartDate">The date when the delay started (day after due date).</param>
    /// <returns>The annual interest rate as a percentage (e.g., 11.50).</returns>
    decimal GetInterestRate(DateTime delayStartDate);
}
