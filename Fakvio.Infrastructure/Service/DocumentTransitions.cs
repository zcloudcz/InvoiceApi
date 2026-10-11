using Fakvio.Application.Common.Extensions;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// THE document state machine (DEVGUIDE §5.6). Every document - issued invoice, credit note, proforma, tax receipt
/// AND received (supplier) invoice - goes through these six transitions and nowhere else:
///
///   Draft --complete--> Completed --mark paid--> Paid / PartiallyPaid
///   Paid / PartiallyPaid --mark unpaid--> Completed --revert to draft--> Draft --delete--> Deleted --restore--> Draft
///
/// Outside these transitions a status is written only in two places: bank-payment matching moves a document
/// between Completed, PartiallyPaid and Paid by its own single rule (<c>PaymentMatchingService.ApplyPaidAmount</c>),
/// and issuing a credit note marks the original invoice Creditnoted (InvoiceService).
///
/// Documents differ ONLY by <see cref="EDocumentType"/>, and the only place that difference shows is the small,
/// explicit "type policy" in the methods below (look for <see cref="IsReceived"/>):
///   - issued documents draw a number from the number sequence, release it on delete and may delete a Completed
///     document only when it is the LAST one of its type;
///   - a received document keeps the supplier's number untouched (never generated / released / cleared), has no
///     "last in sequence" rule (so it can be deleted from Draft only) and sends no invoice webhooks.
///
/// Each method validates the current status, changes the entity and saves. Callers (InvoiceService for issued
/// documents, the ReceivedInvoiceService facade for received ones) only load the entity from THEIR side of the
/// table (<c>IssuedOnly()</c> / <c>ReceivedOnly()</c>) and map the result.
/// </summary>
internal sealed class DocumentTransitions
{
    private readonly TenantDbContext _context;
    private readonly INumberSequenceService? _numberSequenceService; // issued policy only; null where only received documents are handled
    private readonly IExchangeRateService? _exchangeRateService; // optional: without it no ČNB rate is assigned (tests, §4.17)
    private readonly IWebhookPublisher? _webhookPublisher; // issued policy only; optional so manually-constructed instances (tests) work
    private readonly ILogger _logger;

    public DocumentTransitions(
        TenantDbContext context,
        INumberSequenceService? numberSequenceService,
        IExchangeRateService? exchangeRateService,
        IWebhookPublisher? webhookPublisher,
        ILogger logger)
    {
        _context = context;
        _numberSequenceService = numberSequenceService;
        _exchangeRateService = exchangeRateService;
        _webhookPublisher = webhookPublisher;
        _logger = logger;
    }

    /// <summary>The one question the type policy asks: is this a supplier invoice (no number sequence, no webhooks)?</summary>
    private static bool IsReceived(Invoice invoice) => invoice.DocumentType == EDocumentType.ReceivedInvoice;

    // ─── Draft -> Completed ─────────────────────────────────────────────────

    /// <summary>
    /// Completes a Draft (issued: "issue"; received: "approve"). <paramref name="issuedChecks"/> runs after the status
    /// check and before anything changes - InvoiceService passes the issued-only gates there (readiness, number,
    /// variable symbol). A received document has none, so the facade passes null.
    /// </summary>
    public async Task CompleteAsync(Invoice invoice, Func<Task>? issuedChecks, CancellationToken ct)
    {
        if (invoice.Status != EInvoiceStatus.Draft)
            throw new InvalidOperationException($"Invoice is already {invoice.Status}");

        if (!IsReceived(invoice) && issuedChecks != null)
            await issuedChecks();

        await AssignExchangeRateAsync(invoice, ct);
        invoice.Status = EInvoiceStatus.Completed;
        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Fills <see cref="Invoice.ExchangeRate"/> for a non-CZK document being completed (DEVGUIDE §4.17):
    /// the ČNB rate valid for the DUZP (last fixing on or before it, §38 ZDPH). Rules:
    ///   - CZK: no rate (cleared, in case the currency was changed on the draft).
    ///   - Manual rate (value without a ČNB date) or a credit note (inherits its original's rate): kept.
    ///   - Otherwise the ČNB rate is (re)computed; when none is available the rate stays empty and a
    ///     warning is logged - completing is never blocked by a ČNB outage (the PDF then simply omits the rate).
    /// Needs <see cref="Invoice.Currency"/> loaded.
    /// </summary>
    private async Task AssignExchangeRateAsync(Invoice invoice, CancellationToken ct)
    {
        var code = invoice.Currency?.Code;
        if (string.IsNullOrEmpty(code) || code.Equals("CZK", StringComparison.OrdinalIgnoreCase))
        {
            invoice.ExchangeRate = null;
            invoice.ExchangeRateDate = null;
            return;
        }

        var keep = invoice.DocumentType == EDocumentType.CreditNote && invoice.ExchangeRate != null
                   || invoice.ExchangeRate != null && invoice.ExchangeRateDate == null;
        if (keep || _exchangeRateService == null) return;

        // ReceivedDate is only ever set on received documents, so for issued ones this is the DUZP -> issue date chain.
        var duzp = DateOnly.FromDateTime(invoice.TaxableSupplyDate ?? invoice.IssueDate ?? invoice.ReceivedDate ?? DateTime.UtcNow);
        var rate = await _exchangeRateService.GetRateAsync(code, duzp, ct);
        if (rate == null)
            _logger.LogWarning("No ČNB rate for {Currency} on {Date}; {DocumentType} {Id} is completed without an exchange rate",
                code, duzp, invoice.DocumentType, invoice.Id);
        invoice.ExchangeRate = rate?.RatePerUnit;
        invoice.ExchangeRateDate = rate?.ValidFor;
    }

    // ─── Completed / PartiallyPaid -> Paid ──────────────────────────────────

    /// <summary>
    /// Marks the document fully paid. A PartiallyPaid document may be completed by paying the rest by hand.
    /// Returns the amount newly received by this action (the caller needs it for the proforma -> tax receipt hook).
    /// </summary>
    public async Task<decimal> MarkPaidAsync(Invoice invoice, DateTime? paidAt, CancellationToken ct)
    {
        if (invoice.Status != EInvoiceStatus.Completed && invoice.Status != EInvoiceStatus.PartiallyPaid)
            throw new InvalidOperationException(
                $"Only completed invoices can be marked as paid (or partially paid ones). Current status: {invoice.Status}");

        _logger.LogInformation("Marking {DocumentType} {Id} as paid", invoice.DocumentType, invoice.Id);

        // PaidAmount isn't necessarily up to date before this call - manual mark-paid bypasses bank-payment
        // matching - so bring it in line with the new Paid status too (needed for the proforma deduction/DPP amounts).
        var newlyPaid = invoice.TotalWithVat - invoice.PaidAmount;
        invoice.PaidAmount = invoice.TotalWithVat;
        invoice.Status = EInvoiceStatus.Paid;
        invoice.PaidAt = paidAt ?? DateTime.UtcNow;

        // Outbox row is added to the same context (save: false) so it commits atomically with the status change.
        if (!IsReceived(invoice) && _webhookPublisher != null)
            await _webhookPublisher.PublishInvoiceEventAsync(
                Fakvio.Contracts.Dto.Webhook.WebhookEventCatalog.InvoicePaid, invoice, save: false, ct);

        await _context.SaveChangesAsync(ct);
        return newlyPaid;
    }

    // ─── Paid / PartiallyPaid -> Completed ──────────────────────────────────

    /// <summary>
    /// Takes the payment back: removes the PaymentMatch rows, recalculates the MatchStatus of the bank transactions
    /// that were matched and resets the document to Completed. <paramref name="side"/> is the caller's
    /// <c>IssuedOnly()</c> / <c>ReceivedOnly()</c> query, so an id from the other side is "not found" (null).
    /// </summary>
    public async Task<Invoice?> MarkUnpaidAsync(IQueryable<Invoice> side, long invoiceId, CancellationToken ct)
    {
        // Include BankTransaction via PaymentMatch so we can recalculate the transaction's MatchStatus.
        var invoice = await side
            .Include(i => i.PaymentMatch)
                .ThenInclude(m => m.BankTransaction)
                    .ThenInclude(t => t.PaymentMatch)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, ct);

        if (invoice == null)
            return null;

        if (invoice.Status != EInvoiceStatus.Paid && invoice.Status != EInvoiceStatus.PartiallyPaid)
            throw new InvalidOperationException(
                $"Only paid invoices can be marked as unpaid (or partially paid ones). Current status: {invoice.Status}");

        _logger.LogInformation(
            "Marking {DocumentType} {Id} as unpaid (reverting from {Status} to Completed)",
            invoice.DocumentType, invoice.Id, invoice.Status);

        // Collect affected bank transactions BEFORE removing the match rows,
        // so we can still iterate over them and recalculate their MatchStatus.
        var affectedTransactions = invoice.PaymentMatch
            .Select(m => m.BankTransaction)
            .DistinctBy(t => t.Id)
            .ToList();

        // Every payment record tied to this document is deleted so the transaction is free
        // to be matched to a different document in the future.
        _context.PaymentMatch.RemoveRange(invoice.PaymentMatch);

        // After removing this document's matches, sum what is still assigned on the transaction
        // (other documents' matches from the same transaction, if any).
        foreach (var tx in affectedTransactions)
        {
            var stillAssigned = tx.PaymentMatch
                .Where(m => m.InvoiceId != invoiceId)
                .Sum(m => m.MatchedAmount);

            // Mirror PaymentMatchingService.RecalculateTransactionStatus logic.
            if (stillAssigned <= 0)
                tx.MatchStatus = EMatchStatus.Unmatched;
            else if (stillAssigned >= tx.Amount)
                tx.MatchStatus = EMatchStatus.Matched;
            else
                tx.MatchStatus = EMatchStatus.PartiallyMatched;
        }

        invoice.Status = EInvoiceStatus.Completed;
        invoice.PaidAt = null;
        invoice.PaidAmount = 0;

        await _context.SaveChangesAsync(ct);
        return invoice;
    }

    // ─── Completed -> Draft ─────────────────────────────────────────────────

    /// <summary>
    /// Reverts a Completed document to Draft so it can be fully edited. Paid / PartiallyPaid / Creditnoted cannot
    /// (mark unpaid first). The document number is preserved so it can be re-issued with the same number.
    /// </summary>
    public async Task RevertToDraftAsync(Invoice invoice, CancellationToken ct)
    {
        if (invoice.Status != EInvoiceStatus.Completed)
            throw new InvalidOperationException(
                $"Only completed invoices can be reverted to draft. Current status: {invoice.Status}");

        _logger.LogInformation("Reverting {DocumentType} {Id} from Completed to Draft", invoice.DocumentType, invoice.Id);

        invoice.Status = EInvoiceStatus.Draft;
        await _context.SaveChangesAsync(ct);
    }

    // ─── Draft -> Deleted ───────────────────────────────────────────────────

    /// <summary>
    /// Soft-deletes a document. Draft can always be deleted; a Completed ISSUED document only when it is the last
    /// one of its type (continuous numbering required by tax law). Paid / PartiallyPaid / Creditnoted never
    /// (they carry payments or credit notes). Received documents: Draft only - revert to draft first.
    /// </summary>
    public async Task DeleteAsync(Invoice invoice, CancellationToken ct)
    {
        var received = IsReceived(invoice);

        if (invoice.Status == EInvoiceStatus.Deleted)
            throw new InvalidOperationException("Invoice is already deleted.");

        if (invoice.Status is EInvoiceStatus.Paid or EInvoiceStatus.PartiallyPaid or EInvoiceStatus.Creditnoted)
            throw new InvalidOperationException(received
                ? $"Cannot delete a {invoice.Status} invoice. Only a Draft can be deleted - mark it unpaid and revert it to draft first."
                : $"Cannot delete a {invoice.Status} invoice. Only Draft or the last Completed invoice can be deleted.");

        if (invoice.Status == EInvoiceStatus.Completed)
        {
            if (received)
                throw new InvalidOperationException(
                    "Cannot delete a Completed invoice. Only a Draft can be deleted - revert it to draft first.");

            // Deleting a Completed document in the middle of the sequence would break the continuous numbering.
            if (!await IsLastIssuedInvoiceAsync(invoice, ct))
                throw new InvalidOperationException(
                    "Only the last completed invoice can be deleted. " +
                    "This invoice has subsequent documents in the numbering sequence.");
        }

        _logger.LogInformation("Deleting {DocumentType} {Id} (Status={Status}, DocumentNumber={DocNum})",
            invoice.DocumentType, invoice.Id, invoice.Status, invoice.DocumentNumber);

        if (!received)
            await TryReleaseDocumentNumberAsync(invoice, ct);

        invoice.Status = EInvoiceStatus.Deleted;

        if (!received)
        {
            // "invoice.cancelled" - published BEFORE the number is cleared so the payload still carries it, and with
            // save: false so the outbox row commits atomically with the delete.
            if (_webhookPublisher != null) await _webhookPublisher.PublishInvoiceEventAsync(
                Fakvio.Contracts.Dto.Webhook.WebhookEventCatalog.InvoiceCancelled, invoice, save: false, ct);

            // Clearing the number makes it obvious in the DB that it is no longer in use (the unique index
            // IX_Invoice_DocumentNumber already skips Deleted rows). A supplier's number is NOT ours to clear.
            invoice.DocumentNumber = null;
        }

        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Is the given issued document the last non-deleted one of its DocumentType? "Last" = highest ID, because IDs
    /// only grow; simpler and more reliable than parsing numbers with variable prefixes / suffixes.
    /// </summary>
    private async Task<bool> IsLastIssuedInvoiceAsync(Invoice invoice, CancellationToken ct)
    {
        var lastId = await _context.Invoice.IssuedOnly()
            .Where(i => i.DocumentType == invoice.DocumentType
                     && i.Status != EInvoiceStatus.Deleted)
            .MaxAsync(i => (long?)i.Id, ct);

        return lastId == invoice.Id;
    }

    /// <summary>
    /// Releases the document number back to its sequence if the deleted document had the LAST number (the counter
    /// is decremented so the number is reused). If another document was generated after it the number stays
    /// consumed - no gap in the middle. Best-effort: failures are logged but do not block the deletion.
    /// </summary>
    private async Task TryReleaseDocumentNumberAsync(Invoice invoice, CancellationToken ct)
    {
        if (_numberSequenceService == null
            || string.IsNullOrEmpty(invoice.DocumentNumber) || invoice.DocumentNumber == "DRAFT")
            return;

        try
        {
            // Find the default sequence for this document type to get the current counter.
            var sequence = await _context.NumberSequence
                .AsNoTracking()
                .OrderBy(s => s.Id)
                .FirstOrDefaultAsync(
                    s => s.DocumentType == invoice.DocumentType && s.IsDefault && s.IsActive, ct);

            if (sequence == null)
                return;

            // CurrentNumber is the NEXT number to be generated; we pass it as the expected value, so the service
            // only decrements if it still matches (no other document was generated since).
            var released = await _numberSequenceService.TryReleaseLastNumberAsync(
                invoice.DocumentType, sequence.CurrentNumber, ct);

            if (released)
                _logger.LogInformation(
                    "Released document number '{DocNum}' for {DocumentType} - counter decremented, will be reused",
                    invoice.DocumentNumber, invoice.DocumentType);
            else
                _logger.LogDebug(
                    "Document number '{DocNum}' NOT released - not the last in sequence",
                    invoice.DocumentNumber);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to release document number '{DocNum}'", invoice.DocumentNumber);
        }
    }

    // ─── Deleted -> Draft ───────────────────────────────────────────────────

    /// <summary>Restores a soft-deleted document back to Draft so the user can edit and re-complete it.</summary>
    public async Task RestoreAsync(Invoice invoice, CancellationToken ct)
    {
        if (invoice.Status != EInvoiceStatus.Deleted)
            throw new InvalidOperationException("Only deleted invoices can be restored.");

        _logger.LogInformation("Restoring {DocumentType} {Id} to Draft", invoice.DocumentType, invoice.Id);

        invoice.Status = EInvoiceStatus.Draft;
        await _context.SaveChangesAsync(ct);
    }
}
