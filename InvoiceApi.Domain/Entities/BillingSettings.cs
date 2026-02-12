using InvoiceApi.Domain.Common;
using InvoiceApi.Domain.Enums;

namespace InvoiceApi.Domain.Entities;

/// <summary>
/// Billing-specific settings for a client
/// Defines how invoices should be generated for this particular client
/// If not set, system uses default settings
/// </summary>
public class BillingSettings : BaseEntity
{
    /// <summary>
    /// Foreign key to Client (one-to-one relationship)
    /// Each client can have only one set of billing settings
    /// </summary>
    public long ClientId { get; set; }

    /// <summary>
    /// Navigation property to Client
    /// </summary>
    public Client Client { get; set; } = null!;

    /// <summary>
    /// How to calculate due date for invoices
    /// Examples: X days from issue, end of month + X days, etc.
    /// </summary>
    public EDueDateCalculationType DueDateCalculationType { get; set; } = EDueDateCalculationType.DaysFromIssue;

    /// <summary>
    /// Number of days used in due date calculation
    /// Meaning depends on DueDateCalculationType:
    ///   - DaysFromIssue: Days to add to issue date
    ///   - DaysFromEndOfMonth: Days to add to end of month
    ///   - Other types: May not be used
    /// Example: 14 means "payment due in 14 days"
    /// </summary>
    public int DueDays { get; set; } = 14;

    /// <summary>
    /// Optional custom number sequence for this client's invoices
    /// If set, this sequence is used instead of default sequence
    /// Useful for: special clients, different branches, export invoices, etc.
    /// Null = use default sequence
    /// </summary>
    public long? CustomInvoiceNumberSequenceId { get; set; }

    /// <summary>
    /// Navigation property to custom invoice number sequence
    /// </summary>
    public NumberSequence? CustomInvoiceNumberSequence { get; set; }

    /// <summary>
    /// Optional custom number sequence for this client's credit notes
    /// If set, this sequence is used instead of default sequence
    /// Null = use default sequence
    /// </summary>
    public long? CustomCreditNoteNumberSequenceId { get; set; }

    /// <summary>
    /// Navigation property to custom credit note number sequence
    /// </summary>
    public NumberSequence? CustomCreditNoteNumberSequence { get; set; }

    /// <summary>
    /// Optional custom prefix for invoice numbers (overrides sequence prefix)
    /// Example: "EXP-" for export invoices
    /// If null, prefix from NumberSequence is used
    /// </summary>
    public string? InvoiceNumberPrefix { get; set; }

    /// <summary>
    /// Optional custom suffix for invoice numbers (overrides sequence suffix)
    /// Example: "-EU" for EU clients
    /// If null, suffix from NumberSequence is used
    /// </summary>
    public string? InvoiceNumberSuffix { get; set; }

    /// <summary>
    /// Optional custom prefix for credit note numbers (overrides sequence prefix)
    /// If null, prefix from NumberSequence is used
    /// </summary>
    public string? CreditNoteNumberPrefix { get; set; }

    /// <summary>
    /// Optional custom suffix for credit note numbers (overrides sequence suffix)
    /// If null, suffix from NumberSequence is used
    /// </summary>
    public string? CreditNoteNumberSuffix { get; set; }

    /// <summary>
    /// Default payment method for this client (enum for type safety).
    /// Can be overridden on individual invoices.
    /// Null means no default is set — user must choose when creating invoice.
    /// </summary>
    public EPaymentMethod? DefaultPaymentMethod { get; set; }

    /// <summary>
    /// Bank account number for receiving payments from this client
    /// If you have multiple accounts, you can specify which one to use for this client
    /// Null = use default issuer's bank account
    /// </summary>
    public string? BankAccountNumber { get; set; }

    /// <summary>
    /// Optional notes about billing this client
    /// Example: "Always send invoice copy to accountant", "Requires PO number"
    /// </summary>
    public string? Notes { get; set; }
}
