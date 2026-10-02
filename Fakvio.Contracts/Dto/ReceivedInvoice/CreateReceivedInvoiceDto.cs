using System.ComponentModel.DataAnnotations;
using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.ReceivedInvoice;

/// <summary>
/// DTO for creating a new received (incoming) invoice.
/// Represents an expense document from a supplier.
/// </summary>
public class CreateReceivedInvoiceDto
{
    /// <summary>
    /// Supplier's document number (as printed on the invoice).
    /// </summary>
    [StringLength(100)]
    public string? DocumentNumber { get; set; }

    /// <summary>
    /// Supplier (Client ID) — who sent this invoice to us.
    /// </summary>
    [Required]
    public long SupplierId { get; set; }

    /// <summary>
    /// Date when the supplier issued the invoice.
    /// </summary>
    public DateTime? IssueDate { get; set; }

    /// <summary>
    /// Date when we received the invoice.
    /// Defaults to today if not provided.
    /// </summary>
    public DateTime? ReceivedDate { get; set; }

    /// <summary>
    /// Payment due date.
    /// </summary>
    public DateTime? DueDate { get; set; }

    /// <summary>
    /// Date of taxable supply (DUZP) — determines VAT period.
    /// </summary>
    public DateTime? TaxableSupplyDate { get; set; }

    /// <summary>
    /// Supplier's variable symbol for payment.
    /// </summary>
    [StringLength(50)]
    public string? VariableSymbol { get; set; }

    /// <summary>
    /// Currency ID.
    /// </summary>
    [Required]
    public long CurrencyId { get; set; }

    /// <summary>
    /// Manual CZK-per-one-unit exchange rate for a non-CZK document (optional; must be &gt; 0).
    /// Normally omitted — the ČNB rate for the DUZP is assigned when the document is issued/approved.
    /// Allowed only while the document is still a draft / not yet approved.
    /// </summary>
    [Range(typeof(decimal), "0.00000001", "1000000")]
    public decimal? ExchangeRate { get; set; }

    /// <summary>
    /// Payment method.
    /// </summary>
    public EPaymentMethod? PaymentMethod { get; set; }

    /// <summary>
    /// Supplier's bank account for payment.
    /// </summary>
    [StringLength(100)]
    public string? BankAccountNumber { get; set; }

    [StringLength(50)]
    public string? IBAN { get; set; }

    [StringLength(50)]
    public string? SWIFT { get; set; }

    /// <summary>
    /// Optional notes.
    /// </summary>
    [StringLength(5000)]
    public string? Notes { get; set; }

    /// <summary>
    /// Line items — at least one required.
    /// </summary>
    [Required]
    [MinLength(1, ErrorMessage = "At least one item is required")]
    public List<CreateReceivedInvoiceItemDto> Items { get; set; } = new();
}
