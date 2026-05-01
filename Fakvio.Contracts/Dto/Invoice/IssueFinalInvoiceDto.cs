using System.ComponentModel.DataAnnotations;

namespace Fakvio.Contracts.Dto.Invoice;

/// <summary>
/// Request DTO for POST /api/invoice/{proformaId}/issue-final.
/// Creates a standard Invoice from a Proforma, with automatic deduction rows
/// for previously received advance payment, split proportionally across VAT rates.
/// </summary>
public class IssueFinalInvoiceDto
{
    /// <summary>
    /// Line items for the final invoice (the "real" services/goods being invoiced).
    /// At least one billable item is required.
    /// The service will append automatic deduction rows for the advance payment.
    /// </summary>
    [Required]
    [MinLength(1, ErrorMessage = "At least one invoice item is required")]
    public List<CreateInvoiceItemDto> InvoiceItem { get; set; } = new();

    /// <summary>
    /// Amount to deduct from the advance (including VAT) for this particular final invoice.
    /// Used when splitting one proforma into multiple final invoices (1:N).
    ///
    /// When null: the full remaining advance amount (proforma.PaidAmount − already deducted)
    ///            is used for the deduction rows. This is the common case where a single
    ///            final invoice closes the proforma entirely.
    ///
    /// When set:  must be &gt; 0 and ≤ RemainingAdvance. Allows partial deduction so the caller
    ///            can spread one proforma across several final invoices.
    ///
    /// The deduction is always split proportionally across VAT rates as found on the proforma
    /// items, so that each VAT rate gets its own deduction row. This prevents double VAT
    /// (§ 28 odst. 5 zákona č. 235/2004 Sb. o DPH).
    /// </summary>
    public decimal? DeductionAmount { get; set; }

    /// <summary>
    /// Issue date of the final invoice.
    /// When null, defaults to today (UTC).
    /// </summary>
    public DateTime? IssueDate { get; set; }

    /// <summary>
    /// Due date. When null, calculated from the client's billing settings.
    /// </summary>
    public DateTime? DueDate { get; set; }

    /// <summary>
    /// DUZP (taxable supply date) for the final invoice.
    /// When null, defaults to IssueDate.
    /// </summary>
    public DateTime? TaxableSupplyDate { get; set; }

    /// <summary>
    /// Optional notes to print on the final invoice.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Optional number sequence override.
    /// When null, the issuer's default Invoice sequence is used.
    /// </summary>
    public long? NumberSequenceId { get; set; }
}
