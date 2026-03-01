using System.ComponentModel.DataAnnotations;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.InvoiceTemplate;

/// <summary>
/// DTO for creating a new invoice template
/// </summary>
public class CreateInvoiceTemplateDto
{
    /// <summary>
    /// Template name (e.g., "Monthly Hosting Invoice", "Standard Consulting")
    /// </summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional description of what this template is for
    /// </summary>
    [StringLength(2000)]
    public string? Description { get; set; }

    /// <summary>
    /// Type of document - Invoice or CreditNote
    /// </summary>
    [Required]
    public EDocumentType DocumentType { get; set; } = EDocumentType.Invoice;

    /// <summary>
    /// Issuer ID (who issues the invoice)
    /// Usually your company - should be a client with IsIssuer = true
    /// </summary>
    [Required]
    public long IssuerId { get; set; }

    /// <summary>
    /// Optional default client ID — when set, invoices from this template
    /// will pre-select this client. Nullable because templates can be generic.
    /// </summary>
    public long? ClientId { get; set; }

    /// <summary>
    /// Default due date offset in days from issue date
    /// </summary>
    [Range(0, 365, ErrorMessage = "Due date offset must be between 0 and 365 days")]
    public int DueDateOffsetDays { get; set; } = 14;

    /// <summary>
    /// Optional variable symbol — Czech banking requires max 10 digits only.
    /// </summary>
    [StringLength(10)]
    [RegularExpression(@"^\d{0,10}$", ErrorMessage = "Variable symbol must contain only digits (max 10)")]
    public string? VariableSymbol { get; set; }

    /// <summary>
    /// Optional constant symbol
    /// </summary>
    [StringLength(50)]
    public string? ConstantSymbol { get; set; }

    /// <summary>
    /// Optional specific symbol
    /// </summary>
    [StringLength(50)]
    public string? SpecificSymbol { get; set; }

    /// <summary>
    /// Bank account number
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
    /// Payment method — system enum for consistency
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
    /// Optional number sequence override — when set, invoices created from this template
    /// will use this specific sequence instead of the default one for the document type.
    /// </summary>
    public long? NumberSequenceId { get; set; }

    /// <summary>
    /// Invoice line items (template items)
    /// At least one item is required
    /// </summary>
    [Required]
    [MinLength(1, ErrorMessage = "At least one invoice item is required")]
    public List<CreateInvoiceItemDto> InvoiceItem { get; set; } = new();

    // NOTE: HtmlTemplate was removed — PDF rendering templates are now managed
    // via ContentTemplate (EContentTemplateType.InvoicePdf / CreditNotePdf).
}
