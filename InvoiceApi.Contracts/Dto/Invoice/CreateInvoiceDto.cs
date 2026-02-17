using System.ComponentModel.DataAnnotations;
using InvoiceApi.Domain.Enums;

namespace InvoiceApi.Contracts.Dto.Invoice;

/// <summary>
/// DTO for creating a new invoice or credit note
/// </summary>
public class CreateInvoiceDto
{
    /// <summary>
    /// Type of document - Invoice or CreditNote
    /// </summary>
    [Required]
    public EDocumentType DocumentType { get; set; }

    /// <summary>
    /// Client ID (who receives the invoice)
    /// </summary>
    [Required]
    public long ClientId { get; set; }

    /// <summary>
    /// Issuer ID (who issues the invoice)
    /// Usually your company - should be a client with IsIssuer = true
    /// </summary>
    [Required]
    public long IssuerId { get; set; }

    /// <summary>
    /// Issue date - when the invoice is created
    /// If not provided, today's date is used
    /// </summary>
    public DateTime? IssueDate { get; set; }

    /// <summary>
    /// Due date - when payment is due
    /// If not provided, calculated based on client's billing settings
    /// </summary>
    public DateTime? DueDate { get; set; }

    /// <summary>
    /// Date of taxable supply (DUZP)
    /// Required for VAT payers
    /// If not provided, same as IssueDate
    /// </summary>
    public DateTime? TaxableSupplyDate { get; set; }

    /// <summary>
    /// For credit notes: reference to original invoice
    /// Required if DocumentType = CreditNote
    /// </summary>
    public long? OriginalInvoiceId { get; set; }

    /// <summary>
    /// Variable symbol for payment — Czech banking requires max 10 digits only.
    /// If not provided, auto-generated from document number (digits only, truncated to 10).
    /// </summary>
    [StringLength(10)]
    [RegularExpression(@"^\d{0,10}$", ErrorMessage = "Variable symbol must contain only digits (max 10)")]
    public string? VariableSymbol { get; set; }

    /// <summary>
    /// Constant symbol for payment
    /// </summary>
    [StringLength(50)]
    public string? ConstantSymbol { get; set; }

    /// <summary>
    /// Specific symbol for payment
    /// </summary>
    [StringLength(50)]
    public string? SpecificSymbol { get; set; }

    /// <summary>
    /// Bank account number
    /// If not provided, taken from issuer or client billing settings
    /// </summary>
    [StringLength(100)]
    public string? BankAccountNumber { get; set; }

    /// <summary>
    /// IBAN for international payments
    /// </summary>
    [StringLength(50)]
    public string? IBAN { get; set; }

    /// <summary>
    /// SWIFT/BIC code
    /// </summary>
    [StringLength(50)]
    public string? SWIFT { get; set; }

    /// <summary>
    /// Payment method — system enum for consistency and localized display
    /// </summary>
    public EPaymentMethod? PaymentMethod { get; set; }

    /// <summary>
    /// Currency ID (foreign key to Currency table)
    /// </summary>
    [Required]
    public long CurrencyId { get; set; }

    /// <summary>
    /// Optional notes on the invoice
    /// </summary>
    [StringLength(5000)]
    public string? Notes { get; set; }

    /// <summary>
    /// Custom document number
    /// If not provided, generated automatically from number sequence
    /// Only allowed in Draft status
    /// </summary>
    [StringLength(100)]
    public string? CustomDocumentNumber { get; set; }

    /// <summary>
    /// Optional number sequence override — when set, uses this specific sequence
    /// instead of the issuer's default. Typically set when creating from a template
    /// that has a custom NumberSequenceId configured.
    /// </summary>
    public long? NumberSequenceId { get; set; }

    /// <summary>
    /// Invoice line items
    /// At least one item is required
    /// </summary>
    [Required]
    [MinLength(1, ErrorMessage = "At least one invoice item is required")]
    public List<CreateInvoiceItemDto> InvoiceItem { get; set; } = new();
}
