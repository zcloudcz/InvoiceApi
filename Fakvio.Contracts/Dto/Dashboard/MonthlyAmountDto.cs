namespace Fakvio.Contracts.Dto.Dashboard;

/// <summary>
/// One point of a monthly time series on the dashboard (e.g. "Tržby po měsících").
/// </summary>
public class MonthlyAmountDto
{
    /// <summary>Month in "yyyy-MM" format (sortable, locale-independent). The UI formats it for display.</summary>
    public string Month { get; set; } = string.Empty;

    /// <summary>Amount for the month, in CZK, without VAT.</summary>
    public decimal Amount { get; set; }
}

/// <summary>
/// One month of the "Příjmy vs výdaje" (income vs. expense) comparison chart.
/// </summary>
public class IncomeExpenseMonthDto
{
    /// <summary>Month in "yyyy-MM" format.</summary>
    public string Month { get; set; } = string.Empty;

    /// <summary>Issued invoices total for the month (TotalBeforeVat, net of credit notes), in CZK.</summary>
    public decimal Income { get; set; }

    /// <summary>Received invoices total for the month (TotalBeforeVat), in CZK.</summary>
    public decimal Expense { get; set; }
}

/// <summary>
/// Unpaid receivables bucketed by age (days since due date), for the
/// "Neuhrazené pohledávky podle stáří" chart.
/// </summary>
public class ReceivablesAgingDto
{
    /// <summary>
    /// Remaining unpaid amount (TotalWithVat - PaidAmount) for invoices not yet due,
    /// or up to 30 days past due. "Current" bucket in AR-aging terms.
    /// </summary>
    public decimal Bucket0To30 { get; set; }

    /// <summary>Remaining unpaid amount for invoices 31-60 days past due.</summary>
    public decimal Bucket31To60 { get; set; }

    /// <summary>Remaining unpaid amount for invoices 61-90 days past due.</summary>
    public decimal Bucket61To90 { get; set; }

    /// <summary>Remaining unpaid amount for invoices more than 90 days past due.</summary>
    public decimal BucketOver90 { get; set; }
}
