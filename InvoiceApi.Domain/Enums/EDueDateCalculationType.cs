namespace InvoiceApi.Domain.Enums;

/// <summary>
/// How to calculate the due date of an invoice
/// Different clients may have different payment terms
/// </summary>
public enum EDueDateCalculationType
{
    /// <summary>
    /// Due date = Issue date + X days
    /// Example: Invoice issued on Jan 1, DueDays=14 -> Due date = Jan 15
    /// </summary>
    DaysFromIssue = 1,

    /// <summary>
    /// Due date = End of month of issue date + X days
    /// Example: Invoice issued on Jan 15, DueDays=14 -> Due date = Feb 14 (Jan 31 + 14 days)
    /// </summary>
    DaysFromEndOfMonth = 2,

    /// <summary>
    /// Due date = End of next month after issue date
    /// Example: Invoice issued on Jan 15 -> Due date = Feb 28/29
    /// </summary>
    EndOfNextMonth = 3,

    /// <summary>
    /// Due date = End of month of issue date
    /// Example: Invoice issued on Jan 15 -> Due date = Jan 31
    /// </summary>
    EndOfCurrentMonth = 4
}
