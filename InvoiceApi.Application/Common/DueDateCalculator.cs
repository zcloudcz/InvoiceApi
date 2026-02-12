using InvoiceApi.Domain.Enums;

namespace InvoiceApi.Application.Common;

/// <summary>
/// Shared helper for calculating invoice due dates based on EDueDateCalculationType.
/// Used by both the backend InvoiceService and the Blazor UI to ensure consistent behavior.
///
/// The four calculation types are:
///   1. DaysFromIssue — simply adds DueDays to the issue date
///   2. DaysFromEndOfMonth — goes to the last day of the issue month, then adds DueDays
///   3. EndOfNextMonth — returns the last day of the month AFTER the issue month (DueDays ignored)
///   4. EndOfCurrentMonth — returns the last day of the issue month (DueDays ignored)
/// </summary>
public static class DueDateCalculator
{
    /// <summary>
    /// Calculates the due date based on the given calculation type, issue date, and number of days.
    ///
    /// Examples (issueDate = 2026-01-15, dueDays = 14):
    ///   DaysFromIssue      → 2026-01-29  (Jan 15 + 14 days)
    ///   DaysFromEndOfMonth  → 2026-02-14  (Jan 31 + 14 days)
    ///   EndOfNextMonth      → 2026-02-28  (last day of February)
    ///   EndOfCurrentMonth   → 2026-01-31  (last day of January)
    /// </summary>
    /// <param name="issueDate">The invoice issue date (starting point for calculation).</param>
    /// <param name="dueDays">Number of days to add (only used by DaysFromIssue and DaysFromEndOfMonth).</param>
    /// <param name="calculationType">Which algorithm to use for the calculation.</param>
    /// <returns>The calculated due date.</returns>
    public static DateTime Calculate(
        DateTime issueDate,
        int dueDays,
        EDueDateCalculationType calculationType)
    {
        return calculationType switch
        {
            // Type 1: Due date = issue date + X days
            // Simplest calculation — just add the number of days
            EDueDateCalculationType.DaysFromIssue =>
                issueDate.AddDays(dueDays),

            // Type 2: Due date = end of issue month + X days
            // First find the last day of the month, then add days from there
            EDueDateCalculationType.DaysFromEndOfMonth =>
                GetEndOfMonth(issueDate).AddDays(dueDays),

            // Type 3: Due date = last day of the NEXT month
            // DueDays is ignored — always returns end of next month
            EDueDateCalculationType.EndOfNextMonth =>
                GetEndOfMonth(issueDate.AddMonths(1)),

            // Type 4: Due date = last day of the CURRENT month
            // DueDays is ignored — always returns end of current month
            EDueDateCalculationType.EndOfCurrentMonth =>
                GetEndOfMonth(issueDate),

            // Fallback — should never happen, but default to simple DaysFromIssue
            _ => issueDate.AddDays(dueDays)
        };
    }

    /// <summary>
    /// Returns the last day of the month for the given date.
    /// Preserves the time component from the original date.
    ///
    /// Example: 2026-01-15 → 2026-01-31, 2024-02-10 → 2024-02-29 (leap year)
    /// </summary>
    private static DateTime GetEndOfMonth(DateTime date)
    {
        // DaysInMonth returns 28/29/30/31 depending on the month and year
        var lastDay = DateTime.DaysInMonth(date.Year, date.Month);
        return new DateTime(date.Year, date.Month, lastDay, date.Hour, date.Minute, date.Second, date.Kind);
    }
}
