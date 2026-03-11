using Fakvio.Domain.Common;
using Fakvio.Domain.Enums;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Represents a received (incoming) invoice from a supplier.
/// This is an expense document — the opposite of an issued invoice.
/// Used to track costs and calculate input VAT for VAT reports.
///
/// Key differences from issued Invoice:
/// - No Issuer FK (our company is always the recipient)
/// - Supplier is a Client record (but typically not IsIssuer)
/// - No document number generation (number comes from supplier)
/// - Simpler status flow: Received -> Approved -> Paid
/// </summary>
public class ReceivedInvoice : BaseEntity
{
    /// <summary>
    /// Document number as printed on the received invoice.
    /// This is the supplier's document number, not auto-generated.
    /// Example: "FV2024001", "INV-123"
    /// </summary>
    public string? DocumentNumber { get; set; }

    /// <summary>
    /// Current status in the received invoice lifecycle.
    /// Flow: Received -> Approved -> Paid
    /// </summary>
    public EReceivedInvoiceStatus Status { get; set; } = EReceivedInvoiceStatus.Received;

    /// <summary>
    /// Foreign key to Client representing the supplier.
    /// The company that issued this invoice to us.
    /// Uses existing Client entity — suppliers are just clients with a different role.
    /// </summary>
    public long SupplierId { get; set; }

    /// <summary>
    /// Navigation property to the supplier (Client entity).
    /// </summary>
    public Client Supplier { get; set; } = null!;

    /// <summary>
    /// Date when the invoice was issued by the supplier.
    /// Copied from the paper/PDF document.
    /// </summary>
    public DateTime? IssueDate { get; set; }

    /// <summary>
    /// Date when the invoice was received by us.
    /// Important for accounting — determines which period to book it in.
    /// </summary>
    public DateTime? ReceivedDate { get; set; }

    /// <summary>
    /// Payment due date as specified on the invoice.
    /// </summary>
    public DateTime? DueDate { get; set; }

    /// <summary>
    /// Date of taxable supply (DUZP — "datum uskutečnění zdanitelného plnění").
    /// Required for VAT reporting — determines which VAT period this belongs to.
    /// </summary>
    public DateTime? TaxableSupplyDate { get; set; }

    /// <summary>
    /// Variable symbol from the supplier's invoice.
    /// Used when making payment via bank transfer.
    /// </summary>
    public string? VariableSymbol { get; set; }

    /// <summary>
    /// Total amount before VAT (sum of all items).
    /// </summary>
    public decimal TotalBeforeVat { get; set; }

    /// <summary>
    /// Total VAT amount (input VAT — can be deducted in VAT report).
    /// </summary>
    public decimal TotalVat { get; set; }

    /// <summary>
    /// Total amount including VAT — the amount we need to pay.
    /// </summary>
    public decimal TotalWithVat { get; set; }

    /// <summary>
    /// Currency foreign key.
    /// </summary>
    public long CurrencyId { get; set; }

    /// <summary>
    /// Navigation property to Currency.
    /// </summary>
    public Currency Currency { get; set; } = null!;

    /// <summary>
    /// Payment method used or expected.
    /// </summary>
    public EPaymentMethod? PaymentMethod { get; set; }

    /// <summary>
    /// Supplier's bank account number (where to send payment).
    /// </summary>
    public string? BankAccountNumber { get; set; }

    /// <summary>
    /// Supplier's IBAN for international payments.
    /// </summary>
    public string? IBAN { get; set; }

    /// <summary>
    /// Supplier's SWIFT/BIC code.
    /// </summary>
    public string? SWIFT { get; set; }

    /// <summary>
    /// When was this invoice paid (if Status = Paid).
    /// Null until payment is recorded.
    /// </summary>
    public DateTime? PaidAt { get; set; }

    /// <summary>
    /// Optional notes or description.
    /// Example: "Office supplies for Q1", "Hosting services"
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Original filename of the attached document (scan/photo of the invoice).
    /// Null if no attachment uploaded yet. File storage handled separately.
    /// </summary>
    public string? AttachmentFileName { get; set; }

    /// <summary>
    /// MIME type of the attachment (e.g., "application/pdf", "image/jpeg").
    /// </summary>
    public string? AttachmentContentType { get; set; }

    /// <summary>
    /// Collection of line items on this received invoice.
    /// Each item represents one product/service we are being charged for.
    /// </summary>
    public ICollection<ReceivedInvoiceItem> Items { get; set; } = new List<ReceivedInvoiceItem>();
}
