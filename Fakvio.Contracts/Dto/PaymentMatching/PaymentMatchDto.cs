using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.PaymentMatching;

/// <summary>
/// Lightweight read-only projection of a PaymentMatch row.
/// Used by GetPaymentsForInvoiceAsync to expose matched transactions
/// regardless of whether the match is direct (on the invoice itself)
/// or cross-linked via VariableSymbol (proforma ↔ tax receipt for advance).
/// </summary>
public class PaymentMatchDto
{
    /// <summary>PaymentMatch.Id</summary>
    public long Id { get; set; }

    /// <summary>The bank transaction that provided the funds.</summary>
    public long BankTransactionId { get; set; }

    /// <summary>When the transaction settled at the bank.</summary>
    public DateTime TransactionDate { get; set; }

    /// <summary>Amount assigned to this invoice (may be less than the full transaction amount).</summary>
    public decimal MatchedAmount { get; set; }

    /// <summary>Currency of the matched transaction (ISO 4217 code, e.g. "CZK").</summary>
    public string CurrencyCode { get; set; } = "CZK";

    /// <summary>How the match was created (Auto, Manual, …).</summary>
    public EMatchType MatchedBy { get; set; }

    /// <summary>When the PaymentMatch row was created (UTC).</summary>
    public DateTime MatchedAt { get; set; }

    /// <summary>Optional user note attached to the match.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// Id of the invoice this PaymentMatch row is directly stored on.
    /// May differ from the queried invoice when the match is cross-linked
    /// (e.g. querying a Proforma returns matches stored on its TaxReceiptForAdvance).
    /// </summary>
    public long MatchedInvoiceId { get; set; }
}
