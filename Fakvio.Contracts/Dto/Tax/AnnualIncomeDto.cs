namespace Fakvio.Contracts.Dto.Tax;

/// <summary>
/// DTO for auto-calculated annual gross income from issued invoices.
///
/// Junior note: The API sums TotalBeforeVat of all Completed/Paid invoices
/// for the given year (based on IssueDate), grouped by currency.
/// </summary>
public class AnnualIncomeDto
{
    /// <summary>
    /// The year for which the income was calculated.
    /// </summary>
    public int Year { get; set; }

    /// <summary>
    /// Total gross income (sum of TotalBeforeVat from issued invoices).
    /// </summary>
    public decimal GrossIncome { get; set; }

    /// <summary>
    /// Total gross income including VAT (sum of TotalWithVat).
    /// </summary>
    public decimal GrossIncomeWithVat { get; set; }

    /// <summary>
    /// Number of invoices included in the calculation.
    /// </summary>
    public int InvoiceCount { get; set; }

    /// <summary>
    /// Primary currency code (the currency with the highest total).
    /// </summary>
    public string CurrencyCode { get; set; } = "CZK";
}
