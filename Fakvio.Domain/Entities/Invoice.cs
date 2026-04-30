using Fakvio.Domain.Common;
using Fakvio.Domain.Enums;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Represents an invoice or credit note (both use same table)
/// Main document for billing clients
/// </summary>
public class Invoice : BaseEntity
{
    /// <summary>
    /// Type of document - Invoice or CreditNote
    /// Both types share same table and most fields
    /// </summary>
    public EDocumentType DocumentType { get; set; }

    /// <summary>
    /// Current status in the invoice lifecycle
    /// Flow: Draft -> Completed -> Paid
    /// </summary>
    public EInvoiceStatus Status { get; set; } = EInvoiceStatus.Draft;

    /// <summary>
    /// The document number shown to client
    /// Generated from NumberSequence or custom format
    /// Examples: "2024001", "INV-24-0015", "CN-2024-123"
    /// In Draft status, user can still modify this
    /// Once Completed, this is locked
    /// For templates: NULL or "TEMPLATE"
    /// </summary>
    public string? DocumentNumber { get; set; }

    /// <summary>
    /// When was this invoice issued (created date on document)
    /// Usually today's date when invoice is completed
    /// This is the date shown on the invoice
    /// For templates: NULL (will be set when creating invoice from template)
    /// </summary>
    public DateTime? IssueDate { get; set; }

    /// <summary>
    /// When is payment due
    /// Calculated based on IssueDate and client's billing settings
    /// Can be manually overridden by user
    /// For templates: NULL (calculated from DueDateOffsetDays when creating invoice)
    /// </summary>
    public DateTime? DueDate { get; set; }

    /// <summary>
    /// Date of taxable supply (DUZP - "datum uskutečnění zdanitelného plnění")
    /// Required for VAT payers
    /// Usually same as IssueDate, but can be different
    /// Example: Goods delivered on 15th, invoice issued on 20th -> TaxableSupplyDate = 15th
    /// </summary>
    public DateTime? TaxableSupplyDate { get; set; }

    /// <summary>
    /// Foreign key to Client (who receives this invoice)
    /// The customer/client being billed
    /// For templates: Can be NULL (selected when creating invoice from template)
    /// </summary>
    public long? ClientId { get; set; }

    /// <summary>
    /// Navigation property to Client
    /// </summary>
    public Client? Client { get; set; }

    /// <summary>
    /// Foreign key to Issuer (who issues this invoice)
    /// Your company - the one creating and sending the invoice
    /// This is also a Client record but with IsIssuer = true
    /// </summary>
    public long IssuerId { get; set; }

    /// <summary>
    /// Navigation property to Issuer
    /// The company issuing this invoice (your company)
    /// </summary>
    public Client Issuer { get; set; } = null!;

    /// <summary>
    /// Self-referencing FK used by multiple document types:
    /// - Credit note (CreditNote) → the invoice it corrects/cancels.
    /// - Tax receipt for advance (TaxReceiptForAdvance) → the originating pro-forma.
    /// - Final invoice closing a pro-forma → the originating pro-forma (future use).
    /// Null for standalone invoices and pro-forma documents.
    /// </summary>
    public long? OriginalInvoiceId { get; set; }

    /// <summary>
    /// Navigation to the parent document referenced by <see cref="OriginalInvoiceId"/>.
    /// Shared across CreditNote, TaxReceiptForAdvance, and future linked document types.
    /// </summary>
    public Invoice? OriginalInvoice { get; set; }

    /// <summary>
    /// Variable symbol for payment tracking
    /// Usually same as document number or generated from it
    /// Used by client when making bank transfer
    /// Example: If DocumentNumber = "2024001", VariableSymbol might be "2024001"
    /// </summary>
    public string? VariableSymbol { get; set; }

    /// <summary>
    /// Constant symbol for payment categorization
    /// Optional, used in some accounting systems
    /// </summary>
    public string? ConstantSymbol { get; set; }

    /// <summary>
    /// Specific symbol for payment identification
    /// Optional, rarely used
    /// </summary>
    public string? SpecificSymbol { get; set; }

    /// <summary>
    /// Bank account number where payment should be sent
    /// From issuer's data or client-specific billing settings
    /// Format depends on country (IBAN for international)
    /// </summary>
    public string? BankAccountNumber { get; set; }

    /// <summary>
    /// IBAN (International Bank Account Number)
    /// For international payments
    /// </summary>
    public string? IBAN { get; set; }

    /// <summary>
    /// SWIFT/BIC code of the bank
    /// For international payments
    /// </summary>
    public string? SWIFT { get; set; }

    /// <summary>
    /// Payment method enum — displayed on invoice with localized label.
    /// Null means not specified yet (user can set later).
    /// </summary>
    public EPaymentMethod? PaymentMethod { get; set; }

    /// <summary>
    /// Total amount before VAT
    /// Sum of all InvoiceItems amounts
    /// Calculated automatically
    /// </summary>
    public decimal TotalBeforeVat { get; set; }

    /// <summary>
    /// Total VAT amount
    /// Sum of all InvoiceItems VAT amounts
    /// Calculated automatically
    /// 0 if issuer is not VAT payer
    /// </summary>
    public decimal TotalVat { get; set; }

    /// <summary>
    /// Total amount including VAT (final amount to pay)
    /// TotalBeforeVat + TotalVat
    /// This is what client needs to pay
    /// </summary>
    public decimal TotalWithVat { get; set; }

    /// <summary>
    /// Currency foreign key
    /// Links to Currency master data
    /// </summary>
    public long CurrencyId { get; set; }

    /// <summary>
    /// Navigation property to Currency
    /// Contains code, symbol, format, etc.
    /// </summary>
    public Currency Currency { get; set; } = null!;

    /// <summary>
    /// Optional notes/description on the invoice
    /// Any additional information for the client
    /// Example: "Thank you for your business", "Payment terms: ..."
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Has this invoice been exported (to accounting system, PDF, etc.)?
    /// Separate from Status to allow tracking exports independently
    /// Can export multiple times
    /// </summary>
    public bool IsExported { get; set; } = false;

    /// <summary>
    /// When was this invoice last exported
    /// Null if never exported
    /// </summary>
    public DateTime? LastExportedAt { get; set; }

    /// <summary>
    /// Has this invoice been sent by email to client?
    /// Separate from Status to track email delivery
    /// </summary>
    public bool IsSentByEmail { get; set; } = false;

    /// <summary>
    /// When was this invoice last sent by email
    /// Null if never sent
    /// </summary>
    public DateTime? LastSentByEmailAt { get; set; }

    /// <summary>
    /// When was this invoice paid (if Status = Paid)
    /// Null if not yet paid
    /// </summary>
    public DateTime? PaidAt { get; set; }

    /// <summary>
    /// Sum of all PaymentMatch.MatchedAmount rows linked to this invoice.
    /// Denormalized for fast filtering / dashboard KPIs; kept in sync by PaymentMatchingService.
    /// When PaidAmount &gt;= TotalWithVat → Status = Paid.
    /// When 0 &lt; PaidAmount &lt; TotalWithVat → Status = PartiallyPaid.
    /// </summary>
    public decimal PaidAmount { get; set; }

    // Navigation properties

    /// <summary>
    /// Collection of line items on this invoice
    /// Each item represents one product/service being billed
    /// </summary>
    public ICollection<InvoiceItem> InvoiceItem { get; set; } = new List<InvoiceItem>();

    /// <summary>
    /// Inverse navigation for <see cref="OriginalInvoiceId"/>.
    /// Contains all documents that reference this one as their parent:
    /// credit notes, tax receipts for advance, and linked final invoices.
    /// Named "CreditNote" for historical reasons — the collection is shared
    /// across all document types that carry an OriginalInvoiceId.
    /// </summary>
    public ICollection<Invoice> CreditNote { get; set; } = new List<Invoice>();

    /// <summary>
    /// Payment matches linking bank transactions to this invoice.
    /// Used by the Payments panel on InvoiceDetail to show paid/remaining split.
    /// </summary>
    public ICollection<PaymentMatch> PaymentMatch { get; set; } = new List<PaymentMatch>();
}
