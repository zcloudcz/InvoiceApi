using Fakvio.Domain.Common;
using Fakvio.Domain.Enums;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Join row linking a BankTransaction to an invoice.
/// Many-to-many semantics: one transaction can cover multiple invoices
/// (e.g., a bulk payment), one invoice can receive multiple transactions
/// (partial payments). The MatchedAmount field allows splitting a single
/// transaction across multiple invoices.
///
/// Always links to one row of the Invoice table: an issued invoice (incoming payment) or a
/// received one (Invoice.DocumentType = ReceivedInvoice, outgoing payment to a supplier).
/// </summary>
public class PaymentMatch : BaseEntity
{
    /// <summary>FK to the bank transaction providing the funds.</summary>
    public long BankTransactionId { get; set; }

    /// <summary>Navigation to the bank transaction.</summary>
    public BankTransaction BankTransaction { get; set; } = null!;

    /// <summary>FK to the document the payment belongs to (issued or received, see Invoice.DocumentType).</summary>
    public long InvoiceId { get; set; }

    /// <summary>Navigation to the document.</summary>
    public Invoice Invoice { get; set; } = null!;

    /// <summary>Portion of the transaction amount assigned to this invoice.</summary>
    public decimal MatchedAmount { get; set; }

    /// <summary>Who/what created this link — auto matcher, manual user action, suggested.</summary>
    public EMatchType MatchedBy { get; set; }

    /// <summary>When was the link created.</summary>
    public DateTime MatchedAt { get; set; }

    /// <summary>User id for manual matches; null for Auto.</summary>
    public long? MatchedByUserId { get; set; }

    /// <summary>Optional free-text note attached to the match (e.g., user explanation).</summary>
    public string? Note { get; set; }
}
