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

    /// <summary>
    /// When to auto-convert advance invoice → tax receipt.
    /// Only relevant for the issuer's BillingSettings.
    /// </summary>
    public EAdvanceTaxReceiptMode AdvanceTaxReceiptMode { get; set; } = EAdvanceTaxReceiptMode.OnPaymentMatch;
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

    /// <summary>
    /// When to auto-convert advance invoice → tax receipt.
    /// Defaults to OnPaymentMatch — the same behaviour that existed before this setting was added.
    /// </summary>
    public EAdvanceTaxReceiptMode AdvanceTaxReceiptMode { get; set; } = EAdvanceTaxReceiptMode.OnPaymentMatch;
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

    /// <summary>
    /// When to auto-convert advance invoice → tax receipt.
    /// Null = keep existing value (partial update pattern).
    /// </summary>
    public EAdvanceTaxReceiptMode? AdvanceTaxReceiptMode { get; set; }
}

/// <summary>
/// Request body for the dedicated PUT /api/client/issuer/advance-tax-receipt-mode endpoint.
/// Using a wrapper class instead of a plain enum value so the JSON body is a proper object
/// (avoids ambiguity in JSON deserialization of raw enum values).
/// </summary>
public class SetAdvanceTaxReceiptModeDto
{
    /// <summary>
    /// The new auto-conversion mode to store on the issuer's BillingSettings.
    /// </summary>
    public EAdvanceTaxReceiptMode Mode { get; set; }
}
