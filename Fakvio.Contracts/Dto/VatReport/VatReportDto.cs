namespace Fakvio.Contracts.Dto.VatReport;

/// <summary>
/// VAT Report DTO — summarizes output VAT (from issued invoices)
/// and input VAT (from received invoices) for a given period.
///
/// Output VAT = what we charged clients (we owe this to the tax office).
/// Input VAT = what suppliers charged us (we can deduct this).
/// Difference = Output - Input = tax liability (positive) or refund (negative).
/// </summary>
public class VatReportDto
{
    /// <summary>
    /// Start of the reporting period (inclusive).
    /// </summary>
    public DateTime PeriodFrom { get; set; }

    /// <summary>
    /// End of the reporting period (inclusive).
    /// </summary>
    public DateTime PeriodTo { get; set; }

    /// <summary>
    /// Breakdown of output VAT (issued invoices) by VAT rate.
    /// </summary>
    public List<VatReportLineDto> OutputVat { get; set; } = new();

    /// <summary>
    /// Breakdown of input VAT (received invoices) by VAT rate.
    /// </summary>
    public List<VatReportLineDto> InputVat { get; set; } = new();

    /// <summary>
    /// Total output VAT (sum of all OutputVat lines).
    /// </summary>
    public decimal TotalOutputVat { get; set; }

    /// <summary>
    /// Total input VAT (sum of all InputVat lines).
    /// </summary>
    public decimal TotalInputVat { get; set; }

    /// <summary>
    /// Tax liability = TotalOutputVat - TotalInputVat.
    /// Positive = amount owed to tax office.
    /// Negative = refund (nadměrný odpočet).
    /// </summary>
    public decimal TaxLiability { get; set; }

    /// <summary>
    /// Total revenue before VAT (from issued invoices in this period).
    /// </summary>
    public decimal TotalRevenue { get; set; }

    /// <summary>
    /// Total expenses before VAT (from received invoices in this period).
    /// </summary>
    public decimal TotalExpenses { get; set; }

    /// <summary>
    /// Profit = TotalRevenue - TotalExpenses (before VAT).
    /// </summary>
    public decimal Profit { get; set; }

    /// <summary>
    /// Number of issued invoices in this period.
    /// </summary>
    public int IssuedInvoiceCount { get; set; }

    /// <summary>
    /// Number of received invoices in this period.
    /// </summary>
    public int ReceivedInvoiceCount { get; set; }
}

/// <summary>
/// One line in the VAT report — represents a single VAT rate bucket.
/// Example: "21% standard" with aggregated base amount and VAT amount.
/// </summary>
public class VatReportLineDto
{
    /// <summary>
    /// VAT rate percentage (e.g., 21, 12, 0).
    /// </summary>
    public decimal VatRatePercentage { get; set; }

    /// <summary>
    /// Human-readable label (e.g., "DPH 21% - standardní sazba").
    /// </summary>
    public string VatRateLabel { get; set; } = string.Empty;

    /// <summary>
    /// Total base amount (before VAT) for this rate.
    /// </summary>
    public decimal BaseAmount { get; set; }

    /// <summary>
    /// Total VAT amount for this rate.
    /// </summary>
    public decimal VatAmount { get; set; }

    /// <summary>
    /// Number of invoice items at this rate.
    /// </summary>
    public int ItemCount { get; set; }
}
