using Fakvio.Contracts.Dto.PaymentMatching;

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
}

/// <summary>Outcome of a manual match operation.</summary>
public record ManualMatchResult(long PaymentMatchId, decimal InvoicePaidAmount, decimal InvoiceRemaining);
