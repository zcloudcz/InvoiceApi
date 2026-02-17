using InvoiceApi.Contracts.Dto.Invoice;
using InvoiceApi.Domain.Enums;

namespace InvoiceApi.Contracts.Dto.InvoiceTemplate;

/// <summary>
/// DTO for invoice template data
/// Used for API responses
/// </summary>
public class InvoiceTemplateDto
{
    public long Id { get; set; }

    /// <summary>
    /// Template name (e.g., "Monthly Hosting Invoice", "Standard Consulting")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional description of what this template is for
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Type of document - Invoice or CreditNote
    /// </summary>
    public EDocumentType DocumentType { get; set; }

    /// <summary>
    /// Issuer ID (who issues the invoice)
    /// </summary>
    public long IssuerId { get; set; }
    public string IssuerName { get; set; } = string.Empty;

    /// <summary>
    /// Optional default client ID — pre-selects the client when creating an invoice from this template
    /// </summary>
    public long? ClientId { get; set; }
    public string? ClientName { get; set; }

    /// <summary>
    /// Default due date offset in days from issue date
    /// </summary>
    public int DueDateOffsetDays { get; set; } = 14;

    /// <summary>
    /// Optional variable symbol
    /// </summary>
    public string? VariableSymbol { get; set; }

    /// <summary>
    /// Optional constant symbol
    /// </summary>
    public string? ConstantSymbol { get; set; }

    /// <summary>
    /// Optional specific symbol
    /// </summary>
    public string? SpecificSymbol { get; set; }

    /// <summary>
    /// Bank account number
    /// </summary>
    public string? BankAccountNumber { get; set; }

    /// <summary>
    /// IBAN for international payments
    /// </summary>
    public string? IBAN { get; set; }

    /// <summary>
    /// SWIFT/BIC code
    /// </summary>
    public string? SWIFT { get; set; }

    /// <summary>
    /// Payment method — enum for type safety and localized display
    /// </summary>
    public EPaymentMethod? PaymentMethod { get; set; }

    /// <summary>
    /// Currency ID
    /// </summary>
    public long CurrencyId { get; set; }
    public string CurrencyCode { get; set; } = string.Empty;
    public string CurrencySymbol { get; set; } = string.Empty;

    /// <summary>
    /// Optional notes on the invoice
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Invoice line items (template items)
    /// </summary>
    public List<InvoiceItemDto> InvoiceItem { get; set; } = new();

    /// <summary>
    /// How many times this template has been used
    /// </summary>
    public int UsageCount { get; set; }

    /// <summary>
    /// When was this template last used to create an invoice
    /// </summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// Optional number sequence ID for custom numbering on invoices created from this template.
    /// </summary>
    public long? NumberSequenceId { get; set; }

    /// <summary>
    /// Display name of the assigned number sequence (null if using default).
    /// </summary>
    public string? NumberSequenceName { get; set; }

    /// <summary>
    /// Is this template active/available for use?
    /// </summary>
    public bool IsActive { get; set; } = true;

    // NOTE: HtmlTemplate was removed — PDF rendering templates are now managed
    // via ContentTemplate (EContentTemplateType.InvoicePdf / CreditNotePdf).

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
