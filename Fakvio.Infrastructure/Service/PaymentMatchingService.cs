using Fakvio.Application.Common.Extensions;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.PaymentMatching;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

// ponytail: InvoiceService.TryAutoIssueTaxReceiptAsync is called after every positive
// RecalculateInvoice delta below. It is intentionally NOT called from the "unmatch" path
// (negative delta) — voiding an already-issued DPP when a match is undone is a separate,
// more involved feature (credit-note-like reversal of a tax document) and out of scope here.

/// <summary>
/// Default implementation of <see cref="IPaymentMatchingService"/>.
///
/// Algorithm (incoming payments, see PLATBY-ZADANI.md §6):
///   Rule 1 (primary) — exact VS match:
///     - one unpaid invoice with matching VS + exact amount → match
///     - one unpaid invoice with matching VS + lower  amount → partial match
///     - one unpaid invoice with matching VS + higher amount → match + overpayment
///     - several invoices with same VS:
///         * exactly one amount-exact → match that one
///         * otherwise → NeedsReview
///   Rule 2 (fallback) — counterparty account + amount + due-date window (±7 days)
///     - exactly one hit → match
///     - otherwise → Unmatched
///
/// Outgoing payments (#122) are now matched against ReceivedInvoice using the same rules:
///   Rule 1 — VS match against ReceivedInvoice.VariableSymbol
///   Rule 2 — amount + supplier bank account + due-date window
/// </summary>
public class PaymentMatchingService : IPaymentMatchingService
{
    private readonly TenantDbContext _context;
    private readonly INotificationService _notificationService;
    private readonly IInvoiceService _invoiceService;
    private readonly ILogger<PaymentMatchingService> _logger;
    private readonly IWebhookPublisher? _webhookPublisher;

    /// <summary>Payments applied during auto-matching, published as webhooks after the SaveChanges commits them.</summary>
    private readonly List<(Invoice Invoice, decimal Amount, DateTime At, bool BecamePaid)> _pendingPaymentEvents = new();

    /// <summary>How many days before/after the due date we accept for account-based fallback.</summary>
    private const int AccountMatchWindowDays = 7;

    public PaymentMatchingService(
        TenantDbContext context,
        INotificationService notificationService,
        IInvoiceService invoiceService,
        ILogger<PaymentMatchingService> logger,
        IWebhookPublisher? webhookPublisher = null)
    {
        _context = context;
        _notificationService = notificationService;
        _invoiceService = invoiceService;
        _logger = logger;
        _webhookPublisher = webhookPublisher;
    }

    /// <summary>
    /// Adds "payment.received" (and "invoice.paid" when the payment completed the invoice) to the
    /// outbox WITHOUT saving — call right before the business SaveChanges so the events commit
    /// atomically with the match. Never throws (see IWebhookPublisher).
    /// </summary>
    private async Task PublishPaymentAsync(Invoice invoice, decimal amount, DateTime at, bool becamePaid, CancellationToken ct)
    {
        if (_webhookPublisher == null) return;
        await _webhookPublisher.PublishPaymentReceivedAsync(invoice, amount, at, save: false, ct);
        if (becamePaid)
            await _webhookPublisher.PublishInvoiceEventAsync(Contracts.Dto.Webhook.WebhookEventCatalog.InvoicePaid, invoice, save: false, ct);
    }

    /// <inheritdoc />
    public async Task MatchAsync(long bankTransactionId, CancellationToken ct = default)
    {
        var tx = await _context.BankTransaction
            .Include(t => t.BankAccount)
            .Include(t => t.PaymentMatch)
            .FirstOrDefaultAsync(t => t.Id == bankTransactionId, ct);

        if (tx == null)
        {
            _logger.LogWarning("MatchAsync: BankTransaction {Id} not found", bankTransactionId);
            return;
        }

        // Skip already-decided transactions — the caller may hit us twice.
        if (tx.MatchStatus is EMatchStatus.Matched or EMatchStatus.Ignored or EMatchStatus.Recognized)
        {
            _logger.LogDebug("MatchAsync: transaction {Id} already {Status} — skipping", tx.Id, tx.MatchStatus);
            return;
        }

        if (tx.Direction == EPaymentDirection.Incoming)
        {
            await MatchIncomingAsync(tx, ct);
        }
        else
        {
            // Outgoing payments: try to match against ReceivedInvoice (supplier invoices we owe).
            await MatchOutgoingAsync(tx, ct);
        }

        await FlushPendingPaymentEventsAsync(ct);

        await _context.SaveChangesAsync(ct);
    }

    /// <summary>Adds the queued payment webhooks to the outbox (no save) — they commit with the next SaveChanges.</summary>
    private async Task FlushPendingPaymentEventsAsync(CancellationToken ct)
    {
        foreach (var e in _pendingPaymentEvents)
            await PublishPaymentAsync(e.Invoice, e.Amount, e.At, e.BecamePaid, ct);
        _pendingPaymentEvents.Clear();
    }

    /// <inheritdoc />
    public async Task<ManualMatchResult> ManualMatchAsync(
        long bankTransactionId,
        long invoiceId,
        decimal matchedAmount,
        string? note,
        long? userId,
        CancellationToken ct = default)
    {
        if (matchedAmount <= 0)
            throw new ArgumentException("Matched amount must be positive.", nameof(matchedAmount));

        var tx = await _context.BankTransaction
            .Include(t => t.PaymentMatch)
            .FirstOrDefaultAsync(t => t.Id == bankTransactionId, ct)
            ?? throw new InvalidOperationException($"BankTransaction {bankTransactionId} not found.");

        // Either side: an issued invoice (incoming payment) or a received one (outgoing payment to a supplier).
        // The type comes from the stored row; the only difference is the type policy below (isReceived).
        var invoice = await _context.Invoice.AnyDocument()
            .FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
            ?? throw new InvalidOperationException($"Invoice {invoiceId} not found.");
        var isReceived = invoice.DocumentType == EDocumentType.ReceivedInvoice;

        if (invoice.Status == EInvoiceStatus.Deleted)
            throw new InvalidOperationException($"Invoice {invoiceId} is deleted.");

        // A payment belongs only to a completed document (same rule as the automatic candidates): matching a
        // Draft would move it to PartiallyPaid / Paid without ever being completed (no number, no exchange rate).
        if (invoice.Status == EInvoiceStatus.Draft)
            throw new InvalidOperationException($"Invoice {invoiceId} is a draft - complete it before matching a payment.");

        // Never allocate more than the invoice still lacks (same rule as ConfirmAutoMatchAsync).
        var remainingOnInvoice = invoice.TotalWithVat - invoice.PaidAmount;
        if (remainingOnInvoice <= 0)
            throw new InvalidOperationException($"Invoice {invoiceId} has no remaining amount to match.");
        matchedAmount = Math.Min(matchedAmount, remainingOnInvoice);

        // Cannot over-assign the transaction (prevents double-counting).
        var alreadyAssigned = tx.PaymentMatch.Sum(m => m.MatchedAmount);
        var txRemaining = tx.Amount - alreadyAssigned;
        if (matchedAmount > txRemaining)
            throw new InvalidOperationException(
                $"Matched amount {matchedAmount} exceeds the transaction's remaining {txRemaining}.");

        var match = new PaymentMatch
        {
            BankTransactionId = tx.Id,
            InvoiceId = invoice.Id,
            MatchedAmount = matchedAmount,
            MatchedBy = EMatchType.Manual,
            MatchedAt = DateTime.UtcNow,
            MatchedByUserId = userId,
            Note = note,
        };
        _context.PaymentMatch.Add(match);

        // Invoice match wins over registry categorization — drop any recognition.
        tx.RecognizedCounterpartyId = null;

        var wasPaid = invoice.Status == EInvoiceStatus.Paid;
        RecalculateInvoice(invoice, delta: matchedAmount, tx.TransactionDate);
        RecalculateTransactionStatus(tx, alreadyAssigned + matchedAmount);

        // Type policy (same as ConfirmAutoMatchAsync): a received invoice sends no payment webhooks
        // and is never a proforma, so there is no DPP to auto-issue.
        if (!isReceived)
            await PublishPaymentAsync(invoice, matchedAmount, match.MatchedAt, !wasPaid && invoice.Status == EInvoiceStatus.Paid, ct);
        await _context.SaveChangesAsync(ct);

        // Proforma just received (part of) its advance — auto-issue its DPP, if enabled.
        if (!isReceived)
            await _invoiceService.TryAutoIssueTaxReceiptAsync(invoice.Id, matchedAmount, tx.TransactionDate, ct);

        _logger.LogInformation(
            "Manual match: Tx={TxId} → Invoice={InvoiceId} Received={IsReceived} Amount={Amount} User={UserId}",
            tx.Id, invoice.Id, isReceived, matchedAmount, userId);

        await _notificationService.CreateForAllUsersAsync(
            ENotificationType.PaymentMatched,
            "Platba spárována",
            $"Transakce VS {tx.VariableSymbol} ({matchedAmount:N2} {tx.CurrencyCode}) ručně spárována s {(isReceived ? "přijatou fakturou" : "fakturou")} {invoice.DocumentNumber}.",
            invoice.Id,
            isReceived ? "ReceivedInvoice" : "Invoice",
            ct);

        return new ManualMatchResult(match.Id, invoice.PaidAmount, invoice.TotalWithVat - invoice.PaidAmount);
    }

    /// <inheritdoc />
    public async Task UnmatchAsync(long paymentMatchId, string? reason, long? userId, CancellationToken ct = default)
    {
        var match = await _context.PaymentMatch
            .Include(m => m.BankTransaction)
                .ThenInclude(t => t.PaymentMatch)
            .Include(m => m.Invoice)
            .FirstOrDefaultAsync(m => m.Id == paymentMatchId, ct)
            ?? throw new InvalidOperationException($"PaymentMatch {paymentMatchId} not found.");

        // Same rule for issued and received documents: recompute from the matches that REMAIN instead of
        // subtracting this one, so PaidAmount can never drift away from the payments that are really there.
        var remainingOnInvoice = await _context.PaymentMatch
            .Where(m => m.InvoiceId == match.InvoiceId && m.Id != match.Id)
            .SumAsync(m => (decimal?)m.MatchedAmount, ct) ?? 0m;

        // Refused when part of the payment was recorded BY HAND ("mark as paid" on a partially matched document,
        // or a row migrated from the old received table, where any match meant Paid): PaidAmount is then higher
        // than all the matches together, and recomputing from the remaining matches would silently erase the
        // manual part (1000 total, 600 matched, 400 by hand -> unmatch -> 0 paid).
        // The user has to cancel the whole payment on the document instead (mark unpaid removes every match and
        // resets the document). Only Paid / PartiallyPaid are checked - those are exactly the statuses mark unpaid
        // accepts; a Deleted or Creditnoted document could not follow the advice and would keep the transaction
        // blocked forever. Thrown before anything is changed, so a refused unmatch leaves no trace.
        if (match.Invoice.Status is EInvoiceStatus.Paid or EInvoiceStatus.PartiallyPaid
            && match.Invoice.PaidAmount > remainingOnInvoice + match.MatchedAmount)
            throw new InvalidOperationException(
                "This payment cannot be unmatched because part of the document was marked as paid manually. " +
                "Cancel the payment on the document first (mark it as unpaid) - that removes all its matched payments.");

        ApplyPaidAmount(match.Invoice, remainingOnInvoice, match.BankTransaction.TransactionDate);

        _context.PaymentMatch.Remove(match);

        // Recompute transaction aggregate — after removal.
        var remainingOnTx = match.BankTransaction.PaymentMatch
            .Where(m => m.Id != match.Id)
            .Sum(m => m.MatchedAmount);
        RecalculateTransactionStatus(match.BankTransaction, remainingOnTx);

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Unmatch: PaymentMatch={MatchId} Reason={Reason} User={UserId}",
            paymentMatchId, reason, userId);
    }

    /// <inheritdoc />
    public async Task IgnoreAsync(long bankTransactionId, long? userId, CancellationToken ct = default)
    {
        var tx = await _context.BankTransaction
            .FirstOrDefaultAsync(t => t.Id == bankTransactionId, ct)
            ?? throw new InvalidOperationException($"BankTransaction {bankTransactionId} not found.");

        tx.MatchStatus = EMatchStatus.Ignored;
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Ignored: Tx={TxId} User={UserId}", tx.Id, userId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PaymentMatchDto>> GetPaymentsForInvoiceAsync(
        long invoiceId,
        CancellationToken ct = default)
    {
        // Load the requested invoice so we know its type and OriginalInvoiceId.
        var invoice = await _context.Invoice.IssuedOnly()
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == invoiceId, ct);

        if (invoice == null)
        {
            _logger.LogWarning(
                "GetPaymentsForInvoiceAsync: Invoice {Id} not found", invoiceId);
            return Array.Empty<PaymentMatchDto>();
        }

        // Collect the ids of all invoices whose PaymentMatch rows we want to include.
        // Always starts with the invoiceId itself (direct matches).
        var relatedInvoiceIds = new HashSet<long> { invoiceId };

        // Cross-link via OriginalInvoiceId for Proforma and TaxReceiptForAdvance.
        //
        // The full "family" of related invoices for any proforma or DPP is:
        //   { proforma } ∪ { all DPPs that have OriginalInvoiceId == proforma.Id }
        //
        // OriginalInvoiceId is the canonical link — DPPs inherit the same VariableSymbol
        // from the proforma on issue (guaranteed by IssueFromPaidProformaAsync in #5).
        // We expand the set to the full family so that querying any member (proforma OR
        // any of its DPPs) returns the same unified list of payments.
        long? proformaId = null;

        if (invoice.DocumentType == EDocumentType.Proforma)
        {
            proformaId = invoiceId;
        }
        else if (invoice.DocumentType == EDocumentType.TaxReceiptForAdvance
                 && invoice.OriginalInvoiceId.HasValue)
        {
            proformaId = invoice.OriginalInvoiceId.Value;
            // Also include the originating proforma.
            relatedInvoiceIds.Add(proformaId.Value);
        }

        if (proformaId.HasValue)
        {
            // Find all tax receipts for advance (DPPs) linked to this proforma — covers
            // partial-payment scenarios where multiple DPPs exist for one proforma.
            var dppIds = await _context.Invoice.IssuedOnly()
                .AsNoTracking()
                .Where(i =>
                    i.OriginalInvoiceId == proformaId.Value
                    && i.DocumentType == EDocumentType.TaxReceiptForAdvance)
                .Select(i => i.Id)
                .ToListAsync(ct);

            foreach (var id in dppIds)
                relatedInvoiceIds.Add(id);
        }

        // Fetch all PaymentMatch rows for the collected invoice ids in one query.
        var matches = await _context.PaymentMatch
            .AsNoTracking()
            .Include(m => m.BankTransaction)
            .Where(m => relatedInvoiceIds.Contains(m.InvoiceId))
            .OrderBy(m => m.MatchedAt)
            .ToListAsync(ct);

        // Project to DTO. The same PaymentMatch row cannot appear twice (the WHERE clause
        // uses a set of distinct invoice ids), so no extra deduplication is needed.
        return matches
            .Select(m => new PaymentMatchDto
            {
                Id = m.Id,
                BankTransactionId = m.BankTransactionId,
                TransactionDate = m.BankTransaction.TransactionDate,
                MatchedAmount = m.MatchedAmount,
                CurrencyCode = m.BankTransaction.CurrencyCode,
                MatchedBy = m.MatchedBy,
                MatchedAt = m.MatchedAt,
                Note = m.Note,
                MatchedInvoiceId = m.InvoiceId,
            })
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PaymentMatchDto>> GetPaymentsForReceivedInvoiceAsync(
        long receivedInvoiceId,
        CancellationToken ct = default)
    {
        var receivedInvoice = await _context.Invoice.ReceivedOnly()
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == receivedInvoiceId, ct);

        if (receivedInvoice == null)
        {
            _logger.LogWarning(
                "GetPaymentsForReceivedInvoiceAsync: ReceivedInvoice {Id} not found", receivedInvoiceId);
            return Array.Empty<PaymentMatchDto>();
        }

        // ReceivedInvoices have a flat structure — no proforma/DPP cross-linking needed.
        var matches = await _context.PaymentMatch
            .AsNoTracking()
            .Include(m => m.BankTransaction)
            .Where(m => m.InvoiceId == receivedInvoiceId)
            .OrderBy(m => m.MatchedAt)
            .ToListAsync(ct);

        return matches
            .Select(m => new PaymentMatchDto
            {
                Id = m.Id,
                BankTransactionId = m.BankTransactionId,
                TransactionDate = m.BankTransaction.TransactionDate,
                MatchedAmount = m.MatchedAmount,
                CurrencyCode = m.BankTransaction.CurrencyCode,
                MatchedBy = m.MatchedBy,
                MatchedAt = m.MatchedAt,
                Note = m.Note,
                // For received-invoice matches, store 0 in MatchedInvoiceId to signal
                // "this is a ReceivedInvoice match". The panel uses ReceivedInvoiceId instead.
                MatchedInvoiceId = 0,
            })
            .ToList();
    }

    /// <inheritdoc />
    public async Task<AutoMatchProposalDto?> FindAutoMatchForInvoiceAsync(
        long invoiceId,
        CancellationToken ct = default)
    {
        var invoice = await _context.Invoice.IssuedOnly()
            .AsNoTracking()
            .Include(i => i.Currency)
            .Include(i => i.Client)
                .ThenInclude(c => c!.BankAccount)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, ct);

        if (invoice == null)
        {
            _logger.LogWarning("FindAutoMatchForInvoiceAsync: Invoice {Id} not found", invoiceId);
            return null;
        }

        // Only unpaid invoices can be auto-matched.
        if (invoice.Status is EInvoiceStatus.Paid or EInvoiceStatus.Creditnoted or EInvoiceStatus.Deleted or EInvoiceStatus.Draft)
        {
            _logger.LogDebug("FindAutoMatchForInvoiceAsync: Invoice {Id} is {Status} — skip", invoiceId, invoice.Status);
            return null;
        }

        // Load all incoming, unmatched/partially-matched transactions in the same currency.
        var candidates = await _context.BankTransaction
            .AsNoTracking()
            .Where(t =>
                t.Direction == EPaymentDirection.Incoming
                && t.MatchStatus != EMatchStatus.Matched
                && t.MatchStatus != EMatchStatus.Ignored
                && t.CurrencyCode == invoice.Currency.Code)
            .ToListAsync(ct);

        // Rule 1 — VS match.
        if (!string.IsNullOrWhiteSpace(invoice.VariableSymbol))
        {
            var vsMatch = candidates.FirstOrDefault(t => t.VariableSymbol == invoice.VariableSymbol);
            if (vsMatch != null)
                return ToProposal(vsMatch);
        }

        // Rule 2 — amount + counterparty bank account + due-date window.
        if (invoice.DueDate.HasValue && invoice.Client?.BankAccount?.Count > 0)
        {
            var clientAccounts = invoice.Client.BankAccount
                .Select(b => NormalizeAccount(b.AccountNumber))
                .ToHashSet();

            var remaining = invoice.TotalWithVat - invoice.PaidAmount;

            var accountMatch = candidates.FirstOrDefault(t =>
                !string.IsNullOrWhiteSpace(t.CounterpartyAccount)
                && clientAccounts.Contains(NormalizeAccount(t.CounterpartyAccount))
                && Math.Abs((invoice.DueDate.Value - t.TransactionDate).TotalDays) <= AccountMatchWindowDays
                && t.Amount == remaining);

            if (accountMatch != null)
                return ToProposal(accountMatch);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<AutoMatchProposalDto?> FindAutoMatchForReceivedInvoiceAsync(
        long receivedInvoiceId,
        CancellationToken ct = default)
    {
        var receivedInvoice = await _context.Invoice.ReceivedOnly()
            .AsNoTracking()
            .Include(i => i.Currency)
            .FirstOrDefaultAsync(i => i.Id == receivedInvoiceId, ct);

        if (receivedInvoice == null)
        {
            _logger.LogWarning("FindAutoMatchForReceivedInvoiceAsync: ReceivedInvoice {Id} not found", receivedInvoiceId);
            return null;
        }

        // Only approved, unpaid, not deleted received invoices can be auto-matched (Draft = not approved yet,
        // excluded exactly like an issued Draft).
        if (receivedInvoice.Status is EInvoiceStatus.Paid or EInvoiceStatus.Deleted or EInvoiceStatus.Draft)
        {
            _logger.LogDebug("FindAutoMatchForReceivedInvoiceAsync: ReceivedInvoice {Id} is {Status} — skip", receivedInvoiceId, receivedInvoice.Status);
            return null;
        }

        // Load all outgoing, unmatched/partially-matched transactions in the same currency.
        var candidates = await _context.BankTransaction
            .AsNoTracking()
            .Where(t =>
                t.Direction == EPaymentDirection.Outgoing
                && t.MatchStatus != EMatchStatus.Matched
                && t.MatchStatus != EMatchStatus.Ignored
                && t.CurrencyCode == receivedInvoice.Currency.Code)
            .ToListAsync(ct);

        // Rule 1 — VS match.
        if (!string.IsNullOrWhiteSpace(receivedInvoice.VariableSymbol))
        {
            var vsMatch = candidates.FirstOrDefault(t => t.VariableSymbol == receivedInvoice.VariableSymbol);
            if (vsMatch != null)
                return ToProposal(vsMatch);
        }

        // Rule 2 — amount + supplier bank account + due-date window.
        // For received invoices, the supplier's account is stored directly on the entity.
        if (receivedInvoice.DueDate.HasValue && !string.IsNullOrWhiteSpace(receivedInvoice.BankAccountNumber))
        {
            var supplierAccount = NormalizeAccount(receivedInvoice.BankAccountNumber);

            var accountMatch = candidates.FirstOrDefault(t =>
                !string.IsNullOrWhiteSpace(t.CounterpartyAccount)
                && NormalizeAccount(t.CounterpartyAccount) == supplierAccount
                && Math.Abs((receivedInvoice.DueDate.Value - t.TransactionDate).TotalDays) <= AccountMatchWindowDays
                && t.Amount == (receivedInvoice.TotalWithVat - receivedInvoice.PaidAmount));

            if (accountMatch != null)
                return ToProposal(accountMatch);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<TransactionAutoMatchProposalDto?> FindAutoMatchForTransactionAsync(
        long bankTransactionId,
        CancellationToken ct = default)
    {
        var tx = await _context.BankTransaction
            .AsNoTracking()
            .Include(t => t.BankAccount)
            .FirstOrDefaultAsync(t => t.Id == bankTransactionId, ct);

        if (tx == null)
        {
            _logger.LogWarning("FindAutoMatchForTransactionAsync: BankTransaction {Id} not found", bankTransactionId);
            return null;
        }

        // Only unmatched/partially-matched incoming transactions make sense here.
        if (tx.Direction != EPaymentDirection.Incoming)
        {
            _logger.LogDebug(
                "FindAutoMatchForTransactionAsync: Tx {Id} is Outgoing — skip (use received invoice matching instead)",
                bankTransactionId);
            return null;
        }

        if (tx.MatchStatus is EMatchStatus.Matched or EMatchStatus.Ignored or EMatchStatus.Recognized)
        {
            _logger.LogDebug(
                "FindAutoMatchForTransactionAsync: Tx {Id} already {Status} — skip",
                bankTransactionId, tx.MatchStatus);
            return null;
        }

        // Load unpaid issued invoices in the same currency that belong to our bank account's issuer.
        var issuerId = tx.BankAccount?.ClientId;

        var query = _context.Invoice.IssuedOnly()
            .AsNoTracking()
            .Include(i => i.Currency)
            .Include(i => i.Client)
                .ThenInclude(c => c!.BankAccount)
            .Where(i =>
                i.Currency.Code == tx.CurrencyCode
                && i.Status != EInvoiceStatus.Paid
                && i.Status != EInvoiceStatus.Creditnoted
                && i.Status != EInvoiceStatus.Deleted
                && i.Status != EInvoiceStatus.Draft);

        // If the bank account belongs to a specific issuer, narrow candidates to that issuer's invoices.
        if (issuerId.HasValue)
            query = query.Where(i => i.IssuerId == issuerId.Value);

        var candidates = await query.ToListAsync(ct);

        // Rule 1 — Variable symbol (exact match, single hit).
        if (!string.IsNullOrWhiteSpace(tx.VariableSymbol))
        {
            var vsMatches = candidates
                .Where(i => i.VariableSymbol == tx.VariableSymbol)
                .ToList();

            if (vsMatches.Count == 1)
                return ToInvoiceProposal(vsMatches[0]);

            // Multiple VS matches — disambiguate by remaining amount.
            if (vsMatches.Count > 1)
            {
                var exact = vsMatches.FirstOrDefault(i =>
                    (i.TotalWithVat - i.PaidAmount) == tx.Amount);
                if (exact != null)
                    return ToInvoiceProposal(exact);

                // Cannot determine a single winner — treat as NeedsReview; return null.
                return null;
            }
        }

        // Rule 2 — counterparty account (client's bank account) + remaining amount + due-date window.
        if (!string.IsNullOrWhiteSpace(tx.CounterpartyAccount))
        {
            var normalizedCp = NormalizeAccount(tx.CounterpartyAccount);

            var accountMatches = candidates
                .Where(i => i.DueDate.HasValue
                    && i.Client != null
                    && i.Client.BankAccount.Any(b => NormalizeAccount(b.AccountNumber) == normalizedCp)
                    && Math.Abs((i.DueDate.Value - tx.TransactionDate).TotalDays) <= AccountMatchWindowDays
                    && (i.TotalWithVat - i.PaidAmount) == tx.Amount)
                .ToList();

            if (accountMatches.Count == 1)
                return ToInvoiceProposal(accountMatches[0]);
        }

        // ── ReceivedInvoice matching (outgoing payments or incoming with no issued match) ──
        var receivedQuery = _context.Invoice.ReceivedOnly()
            .AsNoTracking()
            .Include(ri => ri.Client)
            .Include(ri => ri.Currency)
            .Where(ri =>
                ri.Status != EInvoiceStatus.Paid
                && ri.Status != EInvoiceStatus.Deleted
                && ri.Status != EInvoiceStatus.Draft);

        var receivedCandidates = await receivedQuery.ToListAsync(ct);

        // Rule 1R — VS match on received invoices
        if (!string.IsNullOrWhiteSpace(tx.VariableSymbol))
        {
            var riVsMatches = receivedCandidates
                .Where(ri => ri.VariableSymbol == tx.VariableSymbol)
                .ToList();

            if (riVsMatches.Count == 1)
                return ToReceivedInvoiceProposal(riVsMatches[0]);
        }

        // Rule 2R — amount match on received invoices
        {
            var riAmountMatches = receivedCandidates
                .Where(ri => (ri.TotalWithVat - ri.PaidAmount) == tx.Amount)
                .ToList();

            if (riAmountMatches.Count == 1)
                return ToReceivedInvoiceProposal(riAmountMatches[0]);
        }

        return null;
    }

    /// <summary>Maps an Invoice entity to a TransactionAutoMatchProposalDto for the UI dialog.</summary>
    private static TransactionAutoMatchProposalDto ToInvoiceProposal(Domain.Entities.Invoice invoice) =>
        new()
        {
            InvoiceId = invoice.Id,
            ReceivedInvoiceId = null,
            DocumentNumber = invoice.DocumentNumber ?? string.Empty,
            ClientName = invoice.Client?.TradingName ?? invoice.Client?.CompanyName,
            TotalWithVat = invoice.TotalWithVat,
            PaidAmount = invoice.PaidAmount,
            Remaining = invoice.TotalWithVat - invoice.PaidAmount,
            CurrencyCode = invoice.Currency?.Code ?? "CZK",
            DueDate = invoice.DueDate,
            VariableSymbol = invoice.VariableSymbol,
        };

    /// <summary>Maps a ReceivedInvoice entity to a TransactionAutoMatchProposalDto.</summary>
    private static TransactionAutoMatchProposalDto ToReceivedInvoiceProposal(Domain.Entities.Invoice ri) =>
        new()
        {
            InvoiceId = null,
            ReceivedInvoiceId = ri.Id,
            DocumentNumber = ri.DocumentNumber ?? string.Empty,
            ClientName = ri.Client?.CompanyName,
            TotalWithVat = ri.TotalWithVat,
            PaidAmount = ri.PaidAmount,
            Remaining = ri.TotalWithVat - ri.PaidAmount,
            CurrencyCode = ri.Currency?.Code ?? "CZK",
            DueDate = ri.DueDate,
            VariableSymbol = ri.VariableSymbol,
        };

    /// <inheritdoc />
    public async Task<ConfirmAutoMatchResult> ConfirmAutoMatchAsync(
        long bankTransactionId,
        long? invoiceId,
        long? receivedInvoiceId,
        long? userId,
        CancellationToken ct = default)
    {
        // Exactly one of (invoiceId, receivedInvoiceId) must be non-null.
        if (invoiceId == null && receivedInvoiceId == null)
            throw new ArgumentException("Either invoiceId or receivedInvoiceId must be provided.");

        if (invoiceId != null && receivedInvoiceId != null)
            throw new ArgumentException("Only one of invoiceId / receivedInvoiceId may be provided at a time.");

        var tx = await _context.BankTransaction
            .Include(t => t.PaymentMatch)
            .FirstOrDefaultAsync(t => t.Id == bankTransactionId, ct)
            ?? throw new InvalidOperationException($"BankTransaction {bankTransactionId} not found.");

        var alreadyAssigned = tx.PaymentMatch.Sum(m => m.MatchedAmount);

        if (invoiceId.HasValue)
        {
            // Confirm match for an issued invoice.
            var invoice = await _context.Invoice.IssuedOnly()
                .FirstOrDefaultAsync(i => i.Id == invoiceId.Value, ct)
                ?? throw new InvalidOperationException($"Invoice {invoiceId} not found.");

            if (invoice.Status == EInvoiceStatus.Deleted)
                throw new InvalidOperationException($"Invoice {invoiceId} is deleted.");

            // Only a completed document can take a payment (see ManualMatchAsync).
            if (invoice.Status == EInvoiceStatus.Draft)
                throw new InvalidOperationException($"Invoice {invoiceId} is a draft - complete it before matching a payment.");

            var remaining = invoice.TotalWithVat - invoice.PaidAmount;
            var matched = Math.Min(tx.Amount - alreadyAssigned, remaining);

            if (matched <= 0)
                throw new InvalidOperationException("No remaining amount to match on this transaction.");

            var match = new PaymentMatch
            {
                BankTransactionId = tx.Id,
                InvoiceId = invoice.Id,
                MatchedAmount = matched,
                MatchedBy = EMatchType.Auto,
                MatchedAt = DateTime.UtcNow,
                MatchedByUserId = userId,
            };
            _context.PaymentMatch.Add(match);

            // Invoice match wins over registry categorization — drop any recognition.
            tx.RecognizedCounterpartyId = null;

            var wasPaid = invoice.Status == EInvoiceStatus.Paid;
            RecalculateInvoice(invoice, delta: matched, tx.TransactionDate);
            RecalculateTransactionStatus(tx, alreadyAssigned + matched);

            await PublishPaymentAsync(invoice, matched, match.MatchedAt, !wasPaid && invoice.Status == EInvoiceStatus.Paid, ct);
            await _context.SaveChangesAsync(ct);

            // Proforma just received (part of) its advance — auto-issue its DPP, if enabled.
            await _invoiceService.TryAutoIssueTaxReceiptAsync(invoice.Id, matched, tx.TransactionDate, ct);

            _logger.LogInformation(
                "ConfirmAutoMatch: Tx={TxId} → Invoice={InvoiceId} Amount={Amount} User={UserId}",
                tx.Id, invoice.Id, matched, userId);

            await _notificationService.CreateForAllUsersAsync(
                ENotificationType.PaymentMatched,
                "Platba spárována",
                $"Transakce VS {tx.VariableSymbol} ({matched:N2} {tx.CurrencyCode}) automaticky spárována s fakturou {invoice.DocumentNumber}.",
                invoice.Id,
                "Invoice",
                ct);

            return new ConfirmAutoMatchResult(match.Id, invoice.PaidAmount, invoice.TotalWithVat - invoice.PaidAmount);
        }
        else
        {
            // Confirm match for a received (supplier) invoice.
            var receivedInvoice = await _context.Invoice.ReceivedOnly()
                .FirstOrDefaultAsync(i => i.Id == receivedInvoiceId!.Value, ct)
                ?? throw new InvalidOperationException($"ReceivedInvoice {receivedInvoiceId} not found.");

            if (receivedInvoice.Status == EInvoiceStatus.Deleted)
                throw new InvalidOperationException($"ReceivedInvoice {receivedInvoiceId} is deleted.");

            // Same rule as for issued invoices: only an APPROVED (Completed) document can take a payment. A Draft
            // (legacy "Received") would otherwise become paid without approve ever running (no exchange rate).
            if (receivedInvoice.Status == EInvoiceStatus.Draft)
                throw new InvalidOperationException(
                    $"ReceivedInvoice {receivedInvoiceId} is not approved yet - approve it before matching a payment.");

            // Same rule as for issued invoices: never allocate more than the document still lacks.
            var remaining = receivedInvoice.TotalWithVat - receivedInvoice.PaidAmount;
            var matched = Math.Min(tx.Amount - alreadyAssigned, remaining);

            if (matched <= 0)
                throw new InvalidOperationException("No remaining amount to match on this transaction.");

            var match = new PaymentMatch
            {
                BankTransactionId = tx.Id,
                InvoiceId = receivedInvoice.Id,
                MatchedAmount = matched,
                MatchedBy = EMatchType.Auto,
                MatchedAt = DateTime.UtcNow,
                MatchedByUserId = userId,
            };
            _context.PaymentMatch.Add(match);

            // Invoice match wins over registry categorization — drop any recognition.
            tx.RecognizedCounterpartyId = null;

            RecalculateInvoice(receivedInvoice, delta: matched, tx.TransactionDate);
            RecalculateTransactionStatus(tx, alreadyAssigned + matched);

            await _context.SaveChangesAsync(ct);

            _logger.LogInformation(
                "ConfirmAutoMatch: Tx={TxId} → ReceivedInvoice={ReceivedInvoiceId} Amount={Amount} User={UserId}",
                tx.Id, receivedInvoice.Id, matched, userId);

            await _notificationService.CreateForAllUsersAsync(
                ENotificationType.PaymentMatched,
                "Platba spárována",
                $"Transakce VS {tx.VariableSymbol} ({matched:N2} {tx.CurrencyCode}) automaticky spárována s přijatou fakturou {receivedInvoice.DocumentNumber}.",
                receivedInvoice.Id,
                "ReceivedInvoice",
                ct);

            return new ConfirmAutoMatchResult(match.Id, receivedInvoice.PaidAmount, receivedInvoice.TotalWithVat - receivedInvoice.PaidAmount);
        }
    }

    // ─── Matching logic ─────────────────────────────────────────────────────

    /// <summary>Tries to match an incoming transaction against outstanding issued invoices.</summary>
    private async Task MatchIncomingAsync(BankTransaction tx, CancellationToken ct)
    {
        // Issuer = the client who owns this bank account (the "our side" of the transaction).
        var issuerId = tx.BankAccount.ClientId;

        var candidates = await _context.Invoice.IssuedOnly()
            .Include(i => i.Currency)
            .Include(i => i.Client)
                .ThenInclude(c => c!.BankAccount)
            .Where(i =>
                i.IssuerId == issuerId
                && i.Status != EInvoiceStatus.Paid
                && i.Status != EInvoiceStatus.Creditnoted
                && i.Status != EInvoiceStatus.Deleted
                && i.Status != EInvoiceStatus.Draft
                && i.Currency.Code == tx.CurrencyCode)
            .ToListAsync(ct);

        // Rule 1 — Variable symbol.
        if (!string.IsNullOrWhiteSpace(tx.VariableSymbol))
        {
            var vsMatches = candidates
                .Where(i => i.VariableSymbol == tx.VariableSymbol)
                .ToList();

            switch (vsMatches.Count)
            {
                case 1:
                    await ApplyAutoMatch(tx, vsMatches[0], ct);
                    return;

                case > 1:
                    // Disambiguate by exact remaining amount.
                    var exact = vsMatches.FirstOrDefault(i =>
                        (i.TotalWithVat - i.PaidAmount) == tx.Amount);
                    if (exact != null)
                    {
                        await ApplyAutoMatch(tx, exact, ct);
                        return;
                    }
                    tx.MatchStatus = EMatchStatus.NeedsReview;
                    return;
            }
        }

        // Rule 2 — counterparty account + amount + due-date window.
        if (!string.IsNullOrWhiteSpace(tx.CounterpartyAccount))
        {
            var normalizedCp = NormalizeAccount(tx.CounterpartyAccount);

            var accountMatches = candidates
                .Where(i => i.DueDate.HasValue
                    && i.Client != null
                    && i.Client.BankAccount.Any(b => NormalizeAccount(b.AccountNumber) == normalizedCp)
                    && Math.Abs((i.DueDate.Value - tx.TransactionDate).TotalDays) <= AccountMatchWindowDays
                    && (i.TotalWithVat - i.PaidAmount) == tx.Amount)
                .ToList();

            if (accountMatches.Count == 1)
            {
                await ApplyAutoMatch(tx, accountMatches[0], ct);
                return;
            }
        }

        // Rule 3 — recognized counterparty registry (no invoice — e.g. VAT refund from FÚ).
        if (await TryRecognizeAsync(tx, ct))
            return;

        // No rule matched.
        tx.MatchStatus = EMatchStatus.Unmatched;
    }

    /// <summary>
    /// Tries to match an outgoing transaction against outstanding received (supplier) invoices.
    /// Called for outgoing bank transactions — we paid something, now check if it matches a
    /// recorded supplier invoice.
    /// </summary>
    private async Task MatchOutgoingAsync(BankTransaction tx, CancellationToken ct)
    {
        // Load all received invoices that are approved and not yet paid, in the same currency.
        // ReceivedInvoice does not have an IssuerId (our company is always the payer),
        // so we filter only by currency and status (Draft = not approved yet, excluded like an issued Draft).
        var candidates = await _context.Invoice.ReceivedOnly()
            .Include(i => i.Currency)
            .Where(i =>
                i.Status != EInvoiceStatus.Paid
                && i.Status != EInvoiceStatus.Deleted
                && i.Status != EInvoiceStatus.Draft
                && i.Currency.Code == tx.CurrencyCode)
            .ToListAsync(ct);

        // Rule 1 — Variable symbol: the transaction's VS should match the supplier invoice's VS.
        if (!string.IsNullOrWhiteSpace(tx.VariableSymbol))
        {
            var vsMatches = candidates
                .Where(i => i.VariableSymbol == tx.VariableSymbol)
                .ToList();

            switch (vsMatches.Count)
            {
                case 1:
                    ApplyAutoMatchReceivedInvoice(tx, vsMatches[0]);
                    return;

                case > 1:
                    // Disambiguate by exact amount.
                    var exact = vsMatches.FirstOrDefault(i => (i.TotalWithVat - i.PaidAmount) == tx.Amount);
                    if (exact != null)
                    {
                        ApplyAutoMatchReceivedInvoice(tx, exact);
                        return;
                    }
                    tx.MatchStatus = EMatchStatus.NeedsReview;
                    return;
            }
        }

        // Rule 2 — counterparty account (the supplier's bank account) + amount + due-date window.
        // The transaction's CounterpartyAccount is the destination we sent money to (the supplier).
        if (!string.IsNullOrWhiteSpace(tx.CounterpartyAccount))
        {
            var normalizedCp = NormalizeAccount(tx.CounterpartyAccount);

            var accountMatches = candidates
                .Where(i =>
                    i.DueDate.HasValue
                    && !string.IsNullOrWhiteSpace(i.BankAccountNumber)
                    && NormalizeAccount(i.BankAccountNumber) == normalizedCp
                    && Math.Abs((i.DueDate.Value - tx.TransactionDate).TotalDays) <= AccountMatchWindowDays
                    && (i.TotalWithVat - i.PaidAmount) == tx.Amount)
                .ToList();

            if (accountMatches.Count == 1)
            {
                ApplyAutoMatchReceivedInvoice(tx, accountMatches[0]);
                return;
            }
        }

        // Rule 3 — recognized counterparty registry (recurring payments without an
        // invoice: social/health insurance, VAT to the tax office, …).
        if (await TryRecognizeAsync(tx, ct))
            return;

        // No rule matched.
        tx.MatchStatus = EMatchStatus.Unmatched;
    }

    /// <summary>
    /// Creates the PaymentMatch row and updates issued invoice + transaction aggregates.
    /// Saves immediately (rather than relying on the caller's later SaveChanges) so that a
    /// just-paid Proforma's PaidAmount is already persisted before the DPP auto-issuance hook
    /// re-reads it to size the tax receipt.
    /// </summary>
    private async Task ApplyAutoMatch(BankTransaction tx, Invoice invoice, CancellationToken ct)
    {
        var remainingOnInvoice = invoice.TotalWithVat - invoice.PaidAmount;
        var matched = Math.Min(tx.Amount, remainingOnInvoice);

        _context.PaymentMatch.Add(new PaymentMatch
        {
            BankTransactionId = tx.Id,
            InvoiceId = invoice.Id,
            MatchedAmount = matched,
            MatchedBy = EMatchType.Auto,
            MatchedAt = DateTime.UtcNow,
        });

        var wasPaid = invoice.Status == EInvoiceStatus.Paid;
        RecalculateInvoice(invoice, delta: matched, tx.TransactionDate);
        RecalculateTransactionStatus(tx, matched);
        _pendingPaymentEvents.Add((invoice, matched, DateTime.UtcNow, !wasPaid && invoice.Status == EInvoiceStatus.Paid));

        // Outbox rows for the payment go in the SAME save as the match (before the DPP issuance,
        // which runs in its own transaction afterwards).
        await FlushPendingPaymentEventsAsync(ct);
        await _context.SaveChangesAsync(ct);

        // Proforma just received (part of) its advance — auto-issue its DPP, if enabled.
        await _invoiceService.TryAutoIssueTaxReceiptAsync(invoice.Id, matched, tx.TransactionDate, ct);

        _logger.LogInformation(
            "Auto match: Tx={TxId} VS={VS} Amount={Amount} → Invoice={InvoiceId}",
            tx.Id, tx.VariableSymbol, matched, invoice.Id);
    }

    /// <summary>
    /// Creates the PaymentMatch row and updates the received invoice status + transaction aggregate.
    /// </summary>
    private void ApplyAutoMatchReceivedInvoice(BankTransaction tx, Invoice receivedInvoice)
    {
        // Only what the document still lacks (a second payment to a partially paid one must not over-allocate).
        var matched = Math.Min(tx.Amount, receivedInvoice.TotalWithVat - receivedInvoice.PaidAmount);

        _context.PaymentMatch.Add(new PaymentMatch
        {
            BankTransactionId = tx.Id,
            InvoiceId = receivedInvoice.Id,
            MatchedAmount = matched,
            MatchedBy = EMatchType.Auto,
            MatchedAt = DateTime.UtcNow,
        });

        RecalculateInvoice(receivedInvoice, delta: matched, tx.TransactionDate);
        RecalculateTransactionStatus(tx, matched);

        _logger.LogInformation(
            "Auto match: Tx={TxId} VS={VS} Amount={Amount} → ReceivedInvoice={ReceivedInvoiceId}",
            tx.Id, tx.VariableSymbol, matched, receivedInvoice.Id);
    }

    /// <summary>Applies a paid-amount delta to the document (issued or received) and recomputes Status + PaidAt.</summary>
    private static void RecalculateInvoice(Invoice invoice, decimal delta, DateTime txDate) =>
        ApplyPaidAmount(invoice, invoice.PaidAmount + delta, txDate);

    /// <summary>
    /// THE payment status rule, shared by issued and received documents: PaidAmount &gt;= total is Paid,
    /// 0 &lt; PaidAmount &lt; total is PartiallyPaid, nothing paid is Completed. No matching path (automatic
    /// candidates, proposals, confirm, manual) accepts a Draft, but one can still get here through legacy data: a
    /// deleted partially paid document restored to Draft keeps its matches, and unmatching them lands here.
    /// A Deleted, Creditnoted or Draft document only gets its PaidAmount updated - a payment change must never
    /// revive a deleted document, turn a credit-noted invoice back into an open (Completed / PartiallyPaid) one,
    /// nor promote a Draft that has not been completed yet.
    /// </summary>
    private static void ApplyPaidAmount(Invoice invoice, decimal paidAmount, DateTime txDate)
    {
        invoice.PaidAmount = paidAmount < 0 ? 0 : paidAmount; // defensive

        if (invoice.Status is EInvoiceStatus.Deleted or EInvoiceStatus.Creditnoted or EInvoiceStatus.Draft)
            return;

        if (invoice.PaidAmount >= invoice.TotalWithVat)
        {
            invoice.Status = EInvoiceStatus.Paid;
            invoice.PaidAt ??= DateTime.SpecifyKind(txDate, DateTimeKind.Utc);
        }
        else if (invoice.PaidAmount > 0)
        {
            invoice.Status = EInvoiceStatus.PartiallyPaid;
            invoice.PaidAt = null;
        }
        else
        {
            // Fully unpaid — revert to Completed (a Draft returns early above).
            invoice.Status = EInvoiceStatus.Completed;
            invoice.PaidAt = null;
        }
    }

    /// <summary>Sets BankTransaction.MatchStatus based on how much is assigned to invoices.</summary>
    private static void RecalculateTransactionStatus(BankTransaction tx, decimal assigned)
    {
        if (assigned <= 0)
        {
            tx.MatchStatus = EMatchStatus.Unmatched;
        }
        else if (assigned >= tx.Amount)
        {
            tx.MatchStatus = EMatchStatus.Matched;
        }
        else
        {
            tx.MatchStatus = EMatchStatus.PartiallyMatched;
        }
    }

    // ─── Recognized counterparties (registry fallback) ──────────────────────

    /// <summary>
    /// Rule 3 fallback: tries to assign the transaction to a recognized-counterparty
    /// registry entry (insurance, tax office, card-payment merchants, …).
    ///
    /// An entry matches when EVERY constraint filled on it holds (empty = wildcard):
    ///   - CounterpartyAccount: normalized equality with the transaction's account
    ///     (a transaction without an account can never satisfy an account constraint),
    ///   - CounterpartyNamePattern: case-insensitive substring of the transaction's
    ///     CounterpartyName OR Message (card payments carry only the merchant name),
    ///   - VS/SS/KS: exact equality.
    /// Entries with neither account nor name pattern never match (service
    /// validation prevents saving them).
    ///
    /// Multiple hits: the most specific entry wins — account counts double
    /// (stronger identifier than a name substring). A tie between entries with
    /// different labels is ambiguous → NeedsReview.
    /// Returns true when it decided the transaction (Recognized or NeedsReview).
    /// </summary>
    private async Task<bool> TryRecognizeAsync(BankTransaction tx, CancellationToken ct)
    {
        var normalizedAccount = string.IsNullOrWhiteSpace(tx.CounterpartyAccount)
            ? null
            : NormalizeAccount(tx.CounterpartyAccount);

        // The registry is tiny (units to tens of rows) — load active entries and
        // compare in memory; NormalizeAccount/OrdinalIgnoreCase Contains are not
        // translatable to SQL anyway.
        var entries = await _context.RecognizedCounterparty
            .Where(r => r.IsActive)
            .ToListAsync(ct);

        bool MatchesNamePattern(string pattern) =>
            (tx.CounterpartyName?.Contains(pattern, StringComparison.OrdinalIgnoreCase) ?? false)
            || (tx.Message?.Contains(pattern, StringComparison.OrdinalIgnoreCase) ?? false);

        var hits = entries.Where(r =>
                // At least one identifying constraint must exist AND hold.
                (!string.IsNullOrWhiteSpace(r.CounterpartyAccount) || !string.IsNullOrWhiteSpace(r.CounterpartyNamePattern))
                && (string.IsNullOrWhiteSpace(r.CounterpartyAccount)
                    || (normalizedAccount != null && NormalizeAccount(r.CounterpartyAccount) == normalizedAccount))
                && (string.IsNullOrWhiteSpace(r.CounterpartyNamePattern) || MatchesNamePattern(r.CounterpartyNamePattern))
                && (string.IsNullOrWhiteSpace(r.VariableSymbol) || r.VariableSymbol == tx.VariableSymbol)
                && (string.IsNullOrWhiteSpace(r.SpecificSymbol) || r.SpecificSymbol == tx.SpecificSymbol)
                && (string.IsNullOrWhiteSpace(r.ConstantSymbol) || r.ConstantSymbol == tx.ConstantSymbol))
            .ToList();

        if (hits.Count == 0)
            return false;

        // Most specific entry wins. Account counts double — it identifies the
        // counterparty exactly, while a name pattern is only a substring guess.
        static int Specificity(RecognizedCounterparty r) =>
            (string.IsNullOrWhiteSpace(r.CounterpartyAccount) ? 0 : 2)
            + (string.IsNullOrWhiteSpace(r.CounterpartyNamePattern) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(r.VariableSymbol) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(r.SpecificSymbol) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(r.ConstantSymbol) ? 0 : 1);

        var top = hits.Max(Specificity);
        var tied = hits.Where(h => Specificity(h) == top).ToList();

        if (tied.Count > 1 && tied.Select(t => t.Label).Distinct().Count() > 1)
        {
            // Two equally-specific entries with different labels — user must decide.
            tx.MatchStatus = EMatchStatus.NeedsReview;
            _logger.LogInformation(
                "Recognition ambiguous: Tx={TxId} matches {Count} registry entries — NeedsReview",
                tx.Id, tied.Count);
            return true;
        }

        tx.RecognizedCounterpartyId = tied[0].Id;
        tx.MatchStatus = EMatchStatus.Recognized;

        _logger.LogInformation(
            "Recognized: Tx={TxId} → RecognizedCounterparty={RcId} ({Label})",
            tx.Id, tied[0].Id, tied[0].Label);

        return true;
    }

    /// <inheritdoc />
    public async Task<int> RescanUnmatchedAsync(CancellationToken ct = default)
    {
        // Only Unmatched transactions are eligible — Matched/Ignored/Recognized are
        // decided, NeedsReview waits for the user, PartiallyMatched belongs to invoices.
        // No account pre-filter: card payments have no account and match by name pattern.
        var unmatched = await _context.BankTransaction
            .Where(t => t.MatchStatus == EMatchStatus.Unmatched)
            .ToListAsync(ct);

        var recognized = 0;
        foreach (var tx in unmatched)
        {
            if (await TryRecognizeAsync(tx, ct) && tx.MatchStatus == EMatchStatus.Recognized)
                recognized++;
        }

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "RescanUnmatched: {Recognized} of {Total} unmatched transactions recognized",
            recognized, unmatched.Count);

        return recognized;
    }

    /// <inheritdoc />
    public async Task AssignRecognizedAsync(
        long bankTransactionId,
        long recognizedCounterpartyId,
        long? userId,
        CancellationToken ct = default)
    {
        var tx = await _context.BankTransaction
            .FirstOrDefaultAsync(t => t.Id == bankTransactionId, ct)
            ?? throw new InvalidOperationException($"BankTransaction {bankTransactionId} not found.");

        // Matched transactions belong to invoices; Ignored ones were explicitly discarded.
        if (tx.MatchStatus is EMatchStatus.Matched or EMatchStatus.PartiallyMatched or EMatchStatus.Ignored)
            throw new InvalidOperationException(
                $"Transaction {bankTransactionId} is {tx.MatchStatus} — unmatch/restore it first.");

        var entry = await _context.RecognizedCounterparty
            .FirstOrDefaultAsync(r => r.Id == recognizedCounterpartyId, ct)
            ?? throw new InvalidOperationException($"RecognizedCounterparty {recognizedCounterpartyId} not found.");

        tx.RecognizedCounterpartyId = entry.Id;
        tx.MatchStatus = EMatchStatus.Recognized;

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "AssignRecognized: Tx={TxId} → RecognizedCounterparty={RcId} ({Label}) User={UserId}",
            tx.Id, entry.Id, entry.Label, userId);
    }

    /// <inheritdoc />
    public async Task UnassignRecognizedAsync(
        long bankTransactionId,
        long? userId,
        CancellationToken ct = default)
    {
        var tx = await _context.BankTransaction
            .Include(t => t.PaymentMatch)
            .FirstOrDefaultAsync(t => t.Id == bankTransactionId, ct)
            ?? throw new InvalidOperationException($"BankTransaction {bankTransactionId} not found.");

        tx.RecognizedCounterpartyId = null;
        // Recompute from invoice matches — a recognized transaction has none,
        // so this normally lands on Unmatched.
        RecalculateTransactionStatus(tx, tx.PaymentMatch.Sum(m => m.MatchedAmount));

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "UnassignRecognized: Tx={TxId} → {Status} User={UserId}", tx.Id, tx.MatchStatus, userId);
    }

    /// <summary>
    /// Normalizes Czech account formats for equality comparison:
    /// strips whitespace and hyphens so "123456-1234567890/0100" matches "1234561234567890/0100".
    /// IBANs are compared case-insensitively with whitespace removed.
    /// </summary>
    private static string NormalizeAccount(string account)
    {
        return new string(account.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray())
            .ToUpperInvariant();
    }

    /// <summary>Projects a BankTransaction to the AutoMatchProposalDto returned to the UI.</summary>
    private static AutoMatchProposalDto ToProposal(BankTransaction tx) => new()
    {
        BankTransactionId = tx.Id,
        TransactionDate = tx.TransactionDate,
        Amount = tx.Amount,
        CurrencyCode = tx.CurrencyCode,
        CounterpartyName = tx.CounterpartyName,
        VariableSymbol = tx.VariableSymbol,
        Message = tx.Message,
    };
}
