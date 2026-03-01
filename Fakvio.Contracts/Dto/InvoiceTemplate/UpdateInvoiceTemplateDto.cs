using System.ComponentModel.DataAnnotations;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.InvoiceTemplate;

/// <summary>
/// DTO for updating an existing invoice template
/// All fields are optional - only provided fields will be updated
/// </summary>
public class UpdateInvoiceTemplateDto
{
    /// <summary>
    /// Template name
    /// </summary>
    [StringLength(200)]
    public string? Name { get; set; }

    /// <summary>
    /// Optional description
    /// </summary>
    [StringLength(2000)]
    public string? Description { get; set; }

    /// <summary>
    /// Optional default client ID for this template
    /// </summary>
    public long? ClientId { get; set; }

    /// <summary>
    /// Default due date offset in days from issue date
    /// </summary>
    [Range(0, 365, ErrorMessage = "Due date offset must be between 0 and 365 days")]
    public int? DueDateOffsetDays { get; set; }

    /// <summary>
    /// Variable symbol — Czech banking requires max 10 digits only.
    /// </summary>
    [StringLength(10)]
    [RegularExpression(@"^\d{0,10}$", ErrorMessage = "Variable symbol must contain only digits (max 10)")]
    public string? VariableSymbol { get; set; }

    /// <summary>
    /// Constant symbol
    /// </summary>
    [StringLength(50)]
    public string? ConstantSymbol { get; set; }

    /// <summary>
    /// Specific symbol
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
    /// Currency ID
    /// </summary>
    public long? CurrencyId { get; set; }

    /// <summary>
    /// Optional notes
    /// </summary>
    [StringLength(5000)]
    public string? Notes { get; set; }

    /// <summary>
    /// Optional number sequence override for invoices created from this template.
    /// Set to null to use the default sequence for the document type.
    /// </summary>
    public long? NumberSequenceId { get; set; }

    /// <summary>
    /// Is this template active/available for use?
    /// </summary>
    public bool? IsActive { get; set; }

    /// <summary>
    /// Updated invoice line items
    /// If provided, replaces all existing items
    /// </summary>
    public List<CreateInvoiceItemDto>? InvoiceItem { get; set; }

    // NOTE: HtmlTemplate was removed — PDF rendering templates are now managed
    // via ContentTemplate (EContentTemplateType.InvoicePdf / CreditNotePdf).
}
