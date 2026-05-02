using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.PaymentMatching;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

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
/// Outgoing payments are out of scope for the MVP (see PLATBY-ZADANI.md Open Q #9).
/// They currently land as Unmatched until a follow-up ticket wires them to ReceivedInvoice.
///
/// Auto-DPP integration (issue #29):
/// After every successful full payment of a Proforma invoice, this service checks the
/// tenant's EAdvanceTaxReceiptMode and — when set to OnPaymentMatch or OnAnyPayment —
/// calls IAdvanceTaxReceiptService to issue the tax receipt for advance payment (DPP).
/// </summary>
public class PaymentMatchingService : IPaymentMatchingService
{
    private readonly TenantDbContext _context;
    private readonly IAdvanceTaxReceiptService _advanceTaxReceiptService;
    private readonly ILogger<PaymentMatchingService> _logger;

    /// <summary>How many days before/after the due date we accept for account-based fallback.</summary>
    private const int AccountMatchWindowDays = 7;

    public PaymentMatchingService(
        TenantDbContext context,
        IAdvanceTaxReceiptService advanceTaxReceiptService,
        ILogger<PaymentMatchingService> logger)
    {
        _context = context;
        _advanceTaxReceiptService = advanceTaxReceiptService;
        _logger = logger;
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
        if (tx.MatchStatus is EMatchStatus.Matched or EMatchStatus.Ignored)
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
            // MVP: outgoing left unmatched. Future: match to ReceivedInvoice.
            tx.MatchStatus = EMatchStatus.Unmatched;
        }

        await _context.SaveChangesAsync(ct);

        // After saving the match, check whether a DPP needs to be issued for a
        // newly-fully-paid pro-forma (EAdvanceTaxReceiptMode.OnPaymentMatch or OnAnyPayment).
        // We must reload the matched invoices AFTER SaveChanges because RecalculateInvoice
        // updates Status in-memory but we need the persisted state for the mode check.
        await TryIssueDppAfterMatchAsync(tx, ct);
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

        var invoice = await _context.Invoice
            .FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
            ?? throw new InvalidOperationException($"Invoice {invoiceId} not found.");

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

        RecalculateInvoice(invoice, delta: matchedAmount, tx.TransactionDate);
        RecalculateTransactionStatus(tx, alreadyAssigned + matchedAmount);

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Manual match: Tx={TxId} → Invoice={InvoiceId} Amount={Amount} User={UserId}",
            tx.Id, invoice.Id, matchedAmount, userId);

        // After a manual match that fully pays a pro-forma, issue DPP when mode is
        // OnPaymentMatch or OnAnyPayment. Manual match is also an "auto" event in the
        // payment-matching pipeline (user chose the invoice but the system matched it),
        // so both modes apply — unlike the manual MarkPaid flow which only triggers OnAnyPayment.
        if (invoice.Status == EInvoiceStatus.Paid
            && invoice.DocumentType == EDocumentType.Proforma)
        {
            await TryIssueDppForProformaAsync(
                invoice,
                matchedAmount: invoice.PaidAmount,
                paymentDate: tx.TransactionDate,
                requireMode: null,   // both OnPaymentMatch and OnAnyPayment trigger here
                ct);
        }

        return new ManualMatchResult(match.Id, invoice.PaidAmount, invoice.TotalWithVat - invoice.PaidAmount);
    }

    /// <inheritdoc />
    public async Task UnmatchAsync(long paymentMatchId, string? reason, long? userId, CancellationToken ct = default)
    {
        var match = await _context.PaymentMatch
            .Include(m => m.BankTransaction)
            .Include(m => m.Invoice)
            .FirstOrDefaultAsync(m => m.Id == paymentMatchId, ct)
            ?? throw new InvalidOperationException($"PaymentMatch {paymentMatchId} not found.");

        if (match.Invoice != null)
        {
            RecalculateInvoice(match.Invoice, delta: -match.MatchedAmount, match.BankTransaction.TransactionDate);
        }

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

    // ─── Matching logic ─────────────────────────────────────────────────────

    /// <summary>Tries to match an incoming transaction against outstanding invoices.</summary>
    private async Task MatchIncomingAsync(BankTransaction tx, CancellationToken ct)
    {
        // Issuer = the client who owns this bank account (the "our side" of the transaction).
        var issuerId = tx.BankAccount.ClientId;

        var candidates = await _context.Invoice
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
                    ApplyAutoMatch(tx, vsMatches[0]);
                    return;

                case > 1:
                    // Disambiguate by exact remaining amount.
                    var exact = vsMatches.FirstOrDefault(i =>
                        (i.TotalWithVat - i.PaidAmount) == tx.Amount);
                    if (exact != null)
                    {
                        ApplyAutoMatch(tx, exact);
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
                ApplyAutoMatch(tx, accountMatches[0]);
                return;
            }
        }

        // No rule matched.
        tx.MatchStatus = EMatchStatus.Unmatched;
    }

    /// <summary>Creates the PaymentMatch row and updates invoice + transaction aggregates.</summary>
    private void ApplyAutoMatch(BankTransaction tx, Invoice invoice)
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

        RecalculateInvoice(invoice, delta: matched, tx.TransactionDate);
        RecalculateTransactionStatus(tx, matched);

        _logger.LogInformation(
            "Auto match: Tx={TxId} VS={VS} Amount={Amount} → Invoice={InvoiceId}",
            tx.Id, tx.VariableSymbol, matched, invoice.Id);
    }

    /// <summary>Applies a paid-amount delta to the invoice and recomputes Status + PaidAt.</summary>
    private static void RecalculateInvoice(Invoice invoice, decimal delta, DateTime txDate)
    {
        invoice.PaidAmount += delta;

        if (invoice.PaidAmount < 0) invoice.PaidAmount = 0; // defensive

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
            // Fully unpaid — revert to Completed (Draft doesn't have matches).
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

    // ─── DPP (advance tax receipt) integration ───────────────────────────────

    /// <summary>
    /// After a successful auto-match save, finds any pro-forma invoices in the transaction's
    /// matched set that just reached Paid status and issues a DPP for them when the mode
    /// allows it (OnPaymentMatch or OnAnyPayment).
    ///
    /// This method runs AFTER SaveChangesAsync so the Status column is committed.
    /// It loads only the PaymentMatch rows for this transaction to avoid a broad query.
    /// </summary>
    private async Task TryIssueDppAfterMatchAsync(BankTransaction tx, CancellationToken ct)
    {
        // Find invoices linked to this transaction that are now fully paid pro-formas.
        var paidProformas = await _context.PaymentMatch
            .Include(m => m.Invoice)
            .Where(m => m.BankTransactionId == tx.Id
                     && m.Invoice != null
                     && m.Invoice.DocumentType == EDocumentType.Proforma
                     && m.Invoice.Status == EInvoiceStatus.Paid)
            .Select(m => m.Invoice!)
            .Distinct()
            .ToListAsync(ct);

        foreach (var proforma in paidProformas)
        {
            // For auto-matches, both OnPaymentMatch and OnAnyPayment modes trigger DPP.
            // The null requireMode means "accept any enabled mode".
            await TryIssueDppForProformaAsync(
                proforma,
                matchedAmount: proforma.PaidAmount,
                paymentDate: tx.TransactionDate,
                requireMode: null,
                ct);
        }
    }

    /// <summary>
    /// Resolves the tenant's EAdvanceTaxReceiptMode from the issuer's BillingSettings,
    /// then calls IAdvanceTaxReceiptService when the mode permits DPP issuance.
    ///
    /// <paramref name="requireMode"/>:
    ///   null  = trigger when mode is OnPaymentMatch OR OnAnyPayment (both auto-match paths)
    ///   OnAnyPayment = trigger ONLY when mode is exactly OnAnyPayment (manual MarkPaid path)
    ///
    /// Disabled mode → never triggers.
    /// </summary>
    private async Task TryIssueDppForProformaAsync(
        Invoice proforma,
        decimal matchedAmount,
        DateTime paymentDate,
        EAdvanceTaxReceiptMode? requireMode,
        CancellationToken ct)
    {
        // Read the mode from the issuer's BillingSettings.
        // Direct DB query is intentional — avoids circular DI and keeps this service stateless.
        var mode = await _context.Client
            .AsNoTracking()
            .Include(c => c.BillingSettings)
            .Where(c => c.Id == proforma.IssuerId)
            .Select(c => c.BillingSettings != null
                ? c.BillingSettings.AdvanceTaxReceiptMode
                : EAdvanceTaxReceiptMode.OnPaymentMatch)
            .FirstOrDefaultAsync(ct);

        // Disabled → never issue DPP automatically.
        if (mode == EAdvanceTaxReceiptMode.Disabled)
        {
            _logger.LogDebug(
                "PaymentMatchingService: DPP skipped for pro-forma {ProformaId} — mode is Disabled",
                proforma.Id);
            return;
        }

        // When a specific mode is required (e.g., the MarkPaid path only fires for OnAnyPayment),
        // check that the configured mode matches.
        if (requireMode.HasValue && mode != requireMode.Value)
        {
            _logger.LogDebug(
                "PaymentMatchingService: DPP skipped for pro-forma {ProformaId} — mode is {Mode}, required {RequiredMode}",
                proforma.Id, mode, requireMode.Value);
            return;
        }

        _logger.LogInformation(
            "PaymentMatchingService: issuing DPP for pro-forma {ProformaId} (mode={Mode})",
            proforma.Id, mode);

        await _advanceTaxReceiptService.IssueFromPaidProformaAsync(
            proforma.Id,
            matchedAmount,
            paymentDate,
            ct);
    }
}
