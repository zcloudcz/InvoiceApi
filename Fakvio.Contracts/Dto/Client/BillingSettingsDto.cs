using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.Client;

/// <summary>
/// DTO for billing settings data (read model)
/// </summary>
public class BillingSettingsDto
{
    public long Id { get; set; }
    public EDueDateCalculationType DueDateCalculationType { get; set; }
    public int DueDays { get; set; }
    public long? CustomInvoiceNumberSequenceId { get; set; }
    public long? CustomCreditNoteNumberSequenceId { get; set; }
    public string? InvoiceNumberPrefix { get; set; }
    public string? InvoiceNumberSuffix { get; set; }
    public string? CreditNoteNumberPrefix { get; set; }
    public string? CreditNoteNumberSuffix { get; set; }

    /// <summary>
    /// Optional file name formats per document type ({{Placeholder}} syntax, no extension),
    /// e.g. "{{DocumentType}}_{{DocumentNumber}}". Empty = inherit from the issuer / system default.
    /// </summary>
    public string? InvoiceFileNameFormat { get; set; }
    public string? CreditNoteFileNameFormat { get; set; }
    public string? ProformaFileNameFormat { get; set; }
    public string? TaxReceiptFileNameFormat { get; set; }

    /// <summary>
    /// Default payment method — now an enum instead of free-text string
    /// </summary>
    public EPaymentMethod? DefaultPaymentMethod { get; set; }

    public string? BankAccountNumber { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// DTO for creating billing settings
/// </summary>
public class CreateBillingSettingsDto
{
    public EDueDateCalculationType DueDateCalculationType { get; set; } = EDueDateCalculationType.DaysFromIssue;
    public int DueDays { get; set; } = 14;
    public long? CustomInvoiceNumberSequenceId { get; set; }
    public long? CustomCreditNoteNumberSequenceId { get; set; }
    public string? InvoiceNumberPrefix { get; set; }
    public string? InvoiceNumberSuffix { get; set; }
    public string? CreditNoteNumberPrefix { get; set; }
    public string? CreditNoteNumberSuffix { get; set; }

    /// <summary>
    /// Optional file name formats per document type ({{Placeholder}} syntax, no extension),
    /// e.g. "{{DocumentType}}_{{DocumentNumber}}". Empty = inherit from the issuer / system default.
    /// </summary>
    public string? InvoiceFileNameFormat { get; set; }
    public string? CreditNoteFileNameFormat { get; set; }
    public string? ProformaFileNameFormat { get; set; }
    public string? TaxReceiptFileNameFormat { get; set; }

    /// <summary>
    /// Default payment method — enum for type safety
    /// </summary>
    public EPaymentMethod? DefaultPaymentMethod { get; set; }

    public string? BankAccountNumber { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// DTO for updating billing settings
/// </summary>
public class UpdateBillingSettingsDto
{
    public EDueDateCalculationType? DueDateCalculationType { get; set; }
    public int? DueDays { get; set; }
    public long? CustomInvoiceNumberSequenceId { get; set; }
    public long? CustomCreditNoteNumberSequenceId { get; set; }
    public string? InvoiceNumberPrefix { get; set; }
    public string? InvoiceNumberSuffix { get; set; }
    public string? CreditNoteNumberPrefix { get; set; }
    public string? CreditNoteNumberSuffix { get; set; }

    /// <summary>
    /// Optional file name formats per document type ({{Placeholder}} syntax, no extension),
    /// e.g. "{{DocumentType}}_{{DocumentNumber}}". Empty = inherit from the issuer / system default.
    /// </summary>
    public string? InvoiceFileNameFormat { get; set; }
    public string? CreditNoteFileNameFormat { get; set; }
    public string? ProformaFileNameFormat { get; set; }
    public string? TaxReceiptFileNameFormat { get; set; }

    /// <summary>
    /// Default payment method — enum for type safety
    /// </summary>
    public EPaymentMethod? DefaultPaymentMethod { get; set; }

    public string? BankAccountNumber { get; set; }
    public string? Notes { get; set; }
}
