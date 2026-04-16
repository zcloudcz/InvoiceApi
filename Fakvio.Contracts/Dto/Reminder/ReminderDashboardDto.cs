namespace Fakvio.Contracts.Dto.Reminder;

/// <summary>
/// Summary statistics for the reminder dashboard widget.
/// Provides a quick overview of the dunning status for the current tenant.
/// </summary>
public class ReminderDashboardDto
{
    /// <summary>
    /// Total number of reminders in Draft status (pending manual send or review).
    /// </summary>
    public int DraftCount { get; set; }

    /// <summary>
    /// Total number of reminders successfully sent.
    /// </summary>
    public int SentCount { get; set; }

    /// <summary>
    /// Total number of reminders that failed to send.
    /// </summary>
    public int FailedCount { get; set; }

    /// <summary>
    /// Total number of overdue invoices (Completed + past due date + not paid).
    /// </summary>
    public int OverdueInvoiceCount { get; set; }

    /// <summary>
    /// Total outstanding amount across all overdue invoices.
    /// </summary>
    public decimal TotalOverdueAmount { get; set; }

    /// <summary>
    /// Total fees charged across all sent reminders (sum of FeeCzk).
    /// </summary>
    public decimal TotalFees { get; set; }

    /// <summary>
    /// Total interest calculated across all sent reminders (sum of InterestCzk).
    /// </summary>
    public decimal TotalInterest { get; set; }

    /// <summary>
    /// Number of reminders created in the last 30 days.
    /// </summary>
    public int RemindersLast30Days { get; set; }

    /// <summary>
    /// Recent reminders for the dashboard table (last 5, ordered by date descending).
    /// </summary>
    public List<ReminderDto> RecentReminders { get; set; } = new();
}
