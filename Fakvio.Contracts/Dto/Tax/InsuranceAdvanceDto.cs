namespace Fakvio.Contracts.Dto.Tax;

/// <summary>
/// DTO for upcoming insurance advance payment notification.
/// Shows when the next social/health insurance payment is due and how much.
///
/// Junior note: Insurance advances are paid monthly by the 20th of the following month.
/// This DTO helps users remember their upcoming payments.
/// </summary>
public class InsuranceAdvanceDto
{
    /// <summary>
    /// Monthly social insurance advance payment amount.
    /// </summary>
    public decimal MonthlySocial { get; set; }

    /// <summary>
    /// Monthly health insurance advance payment amount.
    /// </summary>
    public decimal MonthlyHealth { get; set; }

    /// <summary>
    /// Total monthly insurance payment (social + health).
    /// </summary>
    public decimal MonthlyTotal { get; set; }

    /// <summary>
    /// Next payment due date (typically 20th of the current/next month).
    /// </summary>
    public DateTime NextPaymentDate { get; set; }

    /// <summary>
    /// Days until the next payment is due.
    /// Negative value means payment is overdue.
    /// </summary>
    public int DaysUntilPayment { get; set; }

    /// <summary>
    /// Currency code for the amounts.
    /// </summary>
    public string CurrencyCode { get; set; } = "CZK";

    /// <summary>
    /// Tax regime used to calculate the advances.
    /// </summary>
    public string TaxRegime { get; set; } = string.Empty;
}
