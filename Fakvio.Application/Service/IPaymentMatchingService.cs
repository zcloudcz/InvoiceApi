using Fakvio.Contracts.Dto.PaymentMatching;
// AutoMatchProposalDto, ConfirmAutoMatchRequest/Response live in Fakvio.Contracts.Dto.PaymentMatching

namespace Fakvio.Application.Service;

/// <summary>
/// Core matcher — links BankTransaction rows to Invoice / ReceivedInvoice rows.
/// Tenant-scoped: always runs against the current TenantDbContext.
///
/// The service owns two invariants on Invoice:
///   - PaidAmount = Σ PaymentMatch.MatchedAmount  (kept in sync by all methods)
///   - Status     = Paid when PaidAmount ≥ TotalWithVat
///                  PartiallyPaid when 0 &lt; PaidAmount &lt; TotalWithVat
///                  (previous status otherwise, i.e. Completed)
///
/// See PLATBY-ZADANI.md §6 for the algorithm specification.
/// </summary>
public interface IPaymentMatchingService
{
    /// <summary>
    /// Runs the auto-match algorithm on a single transaction. Idempotent:
    /// re-running it on a Matched transaction is a no-op.
    /// </summary>
    Task MatchAsync(long bankTransactionId, CancellationToken ct = default);

    /// <summary>
    /// Creates a manual match between a transaction and a specific invoice.
    /// Used by the UI's "Match with invoice…" dialog.
    /// </summary>
    Task<ManualMatchResult> ManualMatchAsync(
        long bankTransactionId,
        long invoiceId,
        decimal matchedAmount,
        string? note,
        long? userId,
        CancellationToken ct = default);

    /// <summary>
    /// Removes a PaymentMatch row and recomputes the invoice's PaidAmount/Status.
    /// </summary>
    Task UnmatchAsync(long paymentMatchId, string? reason, long? userId, CancellationToken ct = default);

    /// <summary>
    /// Marks a transaction as not-belonging-to-us (e.g., ATM withdrawal, personal transfer).
    /// </summary>
    Task IgnoreAsync(long bankTransactionId, long? userId, CancellationToken ct = default);

    /// <summary>
    /// Returns all payments visible from a given invoice — combining two sets:
    ///
    ///   1. Direct PaymentMatch rows where PaymentMatch.InvoiceId == invoiceId.
    ///
    ///   2. Cross-linked payments via VariableSymbol:
    ///      - For a Proforma: also returns PaymentMatches stored on any
    ///        TaxReceiptForAdvance (DPP) that references this proforma via
    ///        OriginalInvoiceId (the DPP inherits the same VariableSymbol on
    ///        issue — guaranteed by IssueFromPaidProformaAsync in #5).
    ///      - For a TaxReceiptForAdvance: also returns PaymentMatches stored on
    ///        the originating Proforma (OriginalInvoiceId → proforma).
    ///
    /// This ensures that the Payments panel on both the Proforma detail page and
    /// the DPP detail page shows the same unified set of transactions (#31 AC).
    ///
    /// Duplicate PaymentMatch ids (same row reachable via both paths) are
    /// deduplicated — the caller always gets a distinct list.
    /// </summary>
    /// <param name="invoiceId">Primary key of the invoice to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Distinct list of PaymentMatchDto, ordered by MatchedAt ascending.</returns>
    Task<IReadOnlyList<PaymentMatchDto>> GetPaymentsForInvoiceAsync(
        long invoiceId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all payments linked to a given received (supplier) invoice.
    /// Unlike GetPaymentsForInvoiceAsync there is no cross-linking logic —
    /// received invoices form a flat structure with no proforma/DPP relationships.
    /// </summary>
    /// <param name="receivedInvoiceId">Primary key of the received invoice.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of PaymentMatchDto ordered by MatchedAt ascending.</returns>
    Task<IReadOnlyList<PaymentMatchDto>> GetPaymentsForReceivedInvoiceAsync(
        long receivedInvoiceId,
        CancellationToken ct = default);

    /// <summary>
    /// Searches unmatched BankTransaction rows for a possible automatic match
    /// against a specific issued invoice. Called by the UI's "Auto-match" button.
    ///
    /// Search strategy (same as MatchAsync, but targeted):
    ///   1. VS match: transaction.VariableSymbol == invoice.VariableSymbol
    ///   2. Amount + account + due-date window (±7 days)
    ///
    /// Returns at most one candidate — the best match found. Returns null when
    /// no suitable unmatched transaction is found.
    /// </summary>
    Task<AutoMatchProposalDto?> FindAutoMatchForInvoiceAsync(
        long invoiceId,
        CancellationToken ct = default);

    /// <summary>
    /// Same as FindAutoMatchForInvoiceAsync but searches for outgoing transactions
    /// that match the given received (supplier) invoice.
    /// </summary>
    Task<AutoMatchProposalDto?> FindAutoMatchForReceivedInvoiceAsync(
        long receivedInvoiceId,
        CancellationToken ct = default);

    /// <summary>
    /// Reverse auto-match: given an unmatched incoming BankTransaction, searches
    /// unpaid issued invoices for the best candidate match.
    ///
    /// Search strategy (mirrors MatchIncomingAsync):
    ///   1. VS match: transaction.VariableSymbol == invoice.VariableSymbol (single hit)
    ///   2. Amount + counterparty account + due-date window (±7 days)
    ///
    /// Returns at most one candidate. Returns null when no suitable invoice is found.
    /// Used by the Payments page "Auto-match" button per unmatched transaction.
    /// </summary>
    Task<TransactionAutoMatchProposalDto?> FindAutoMatchForTransactionAsync(
        long bankTransactionId,
        CancellationToken ct = default);

    /// <summary>
    /// Confirms an auto-match proposal by creating a PaymentMatch row and
    /// marking the invoice as Paid (or PartiallyPaid).
    /// Used by the UI after the user clicks "Confirm" in the auto-match dialog.
    /// </summary>
    Task<ConfirmAutoMatchResult> ConfirmAutoMatchAsync(
        long bankTransactionId,
        long? invoiceId,
        long? receivedInvoiceId,
        long? userId,
        CancellationToken ct = default);
}

/// <summary>Outcome of a manual match operation.</summary>
public record ManualMatchResult(long PaymentMatchId, decimal InvoicePaidAmount, decimal InvoiceRemaining);

/// <summary>Outcome of ConfirmAutoMatchAsync.</summary>
public record ConfirmAutoMatchResult(long PaymentMatchId, decimal PaidAmount, decimal Remaining);
