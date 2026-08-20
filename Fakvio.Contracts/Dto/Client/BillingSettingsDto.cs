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
    /// Default payment method — enum for type safety
    /// </summary>
    public EPaymentMethod? DefaultPaymentMethod { get; set; }

    public string? BankAccountNumber { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Request body of PUT /api/client/issuer/advance-tax-receipt-mode.
///
/// A wrapper object instead of a bare enum value keeps the payload a proper JSON
/// object ({ "mode": "OnPaymentMatch" }), which is what every other endpoint in this
/// API accepts and what the Functions host deserializes without special-casing.
/// </summary>
public class SetAdvanceTaxReceiptModeDto
{
    /// <summary>
    /// New auto-conversion mode for the issuer.
    /// </summary>
    public EAdvanceTaxReceiptMode Mode { get; set; }
}
