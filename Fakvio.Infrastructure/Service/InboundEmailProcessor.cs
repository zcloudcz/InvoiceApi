using System.Security.Cryptography;
using System.Text;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Default <see cref="IInboundEmailProcessor"/>.
///
/// Strict ordering (so a partial failure leaves a recoverable state):
///   1) Resolve mailbox + active window checks — skip early if gated.
///   2) Compute dedupe hash; skip if already ingested.
///   3) Persist InboundEmail (truncated bodies) → we ALWAYS keep the archive.
///   4) Invoke the AI parser.
///      - success → create BankTransaction (skip if duplicate), run matcher.
///      - failure → mark InboundEmail with NeedsReview / Failed + error message.
/// </summary>
public class InboundEmailProcessor : IInboundEmailProcessor
{
    /// <summary>Max body size we persist per email. Larger bodies are truncated.</summary>
    private const int MaxBodyChars = 1_000_000; // ~1 MB UTF-8

    private readonly TenantDbContext _tenant;
    private readonly IBankEmailParser _parser;
    private readonly IPaymentMatchingService _matcher;
    private readonly ILogger<InboundEmailProcessor> _logger;

    public InboundEmailProcessor(
        TenantDbContext tenant,
        IBankEmailParser parser,
        IPaymentMatchingService matcher,
        ILogger<InboundEmailProcessor> logger)
    {
        _tenant = tenant;
        _parser = parser;
        _matcher = matcher;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<EParseStatus> ProcessAsync(InboundEmailPayload payload, long? companyId, CancellationToken ct = default)
    {
        var mailbox = await _tenant.BankAccountMailbox
            .Include(m => m.BankAccount)
            .FirstOrDefaultAsync(m => m.Id == payload.BankAccountMailboxId, ct)
            ?? throw new InvalidOperationException(
                $"BankAccountMailbox {payload.BankAccountMailboxId} not found in current tenant.");

        // Guard — IMAP worker should have filtered these already, but defense-in-depth is cheap.
        if (!mailbox.IsActive)
        {
            _logger.LogInformation(
                "Mailbox {MailboxId} is deactivated — skipping email {MessageId}",
                mailbox.Id, payload.MessageId);
            return EParseStatus.Ignored;
        }

        var effectiveDate = payload.EmailDate ?? payload.ServerReceivedAt;
        if (effectiveDate < mailbox.ActiveFrom)
        {
            _logger.LogInformation(
                "Email {MessageId} older than ActiveFrom ({Date} < {ActiveFrom}) — skipping",
                payload.MessageId, effectiveDate, mailbox.ActiveFrom);
            return EParseStatus.Ignored;
        }

        // Idempotency — the same message might be delivered twice on IMAP reconnect.
        var hash = ComputeDeduplicationHash(mailbox.Id, payload);
        var existing = await _tenant.InboundEmail.FirstOrDefaultAsync(
            e => e.BankAccountMailboxId == mailbox.Id && e.DeduplicationHash == hash, ct);
        if (existing != null)
        {
            _logger.LogDebug(
                "Duplicate inbound email (hash {Hash}) — already ingested as {Id}", hash, existing.Id);
            return existing.ParseStatus;
        }

        // Persist archive row FIRST — even if everything else fails, we don't lose the email.
        var textBody = Truncate(payload.TextBody);
        var htmlBody = Truncate(payload.HtmlBody);
        var truncated = textBody.Truncated || htmlBody.Truncated;

        var archive = new InboundEmail
        {
            BankAccountMailboxId = mailbox.Id,
            MessageId = payload.MessageId,
            ImapUid = payload.ImapUid,
            ServerReceivedAt = payload.ServerReceivedAt,
            FromAddress = payload.FromAddress,
            FromDisplayName = payload.FromDisplayName,
            ToAddress = payload.ToAddress,
            Subject = payload.Subject,
            EmailDate = payload.EmailDate,
            TextBody = textBody.Value,
            HtmlBody = htmlBody.Value,
            BodyTruncated = truncated,
            DeduplicationHash = hash,
            ParseStatus = EParseStatus.Pending,
            ParseAttempts = 0,
        };
        _tenant.InboundEmail.Add(archive);

        // Also bump mailbox stats eagerly so the UI shows "Received 43 / last 10 min ago".
        mailbox.LastEmailReceivedAt = payload.ServerReceivedAt;
        mailbox.EmailsReceivedCount += 1;

        await _tenant.SaveChangesAsync(ct);

        // Now try to parse + match.
        archive.ParseAttempts += 1;

        BankEmailParsed? parsed;
        try
        {
            parsed = await _parser.ParseAsync(new BankEmailInput(
                From: payload.FromAddress,
                Subject: payload.Subject ?? string.Empty,
                TextBody: payload.TextBody,
                HtmlBody: payload.HtmlBody,
                ReceivedAt: effectiveDate), companyId, ct);
        }
        catch (Exception ex)
        {
            archive.ParseStatus = EParseStatus.Failed;
            archive.ParseError = ex.Message;
            await _tenant.SaveChangesAsync(ct);
            _logger.LogError(ex, "Parser threw for email {MessageId}", payload.MessageId);
            return EParseStatus.Failed;
        }

        if (parsed == null)
        {
            archive.ParseStatus = EParseStatus.NeedsReview;
            archive.ParseError = "AI could not produce a high-confidence payment extraction.";
            await _tenant.SaveChangesAsync(ct);
            return EParseStatus.NeedsReview;
        }

        // Translate to a BankTransaction — but only if we don't already have one for
        // the same (account, dedupe hash) to keep ingestion idempotent across reparses.
        var txHash = ComputeTransactionHash(mailbox.BankAccountId, parsed);
        var existingTx = await _tenant.BankTransaction
            .FirstOrDefaultAsync(t => t.BankAccountId == mailbox.BankAccountId && t.DeduplicationHash == txHash, ct);

        BankTransaction transaction;
        if (existingTx != null)
        {
            transaction = existingTx;
        }
        else
        {
            transaction = new BankTransaction
            {
                BankAccountId = mailbox.BankAccountId,
                DeduplicationHash = txHash,
                TransactionDate = parsed.TransactionDate,
                Amount = parsed.Amount,
                CurrencyCode = parsed.CurrencyCode,
                Direction = parsed.Direction,
                VariableSymbol = parsed.VariableSymbol,
                ConstantSymbol = parsed.ConstantSymbol,
                SpecificSymbol = parsed.SpecificSymbol,
                CounterpartyAccount = parsed.CounterpartyAccount,
                CounterpartyName = parsed.CounterpartyName,
                Message = parsed.Message,
                ImportSource = EImportSource.InboundEmail,
                ParserConfidence = parsed.Confidence,
                ParserModel = parsed.ModelUsed,
                MatchStatus = EMatchStatus.Unmatched,
                RawPayload = TruncateRaw(payload.TextBody ?? payload.HtmlBody ?? string.Empty),
            };
            _tenant.BankTransaction.Add(transaction);
        }

        archive.BankTransaction = transaction;
        archive.ParseStatus = EParseStatus.Parsed;
        archive.ParseError = null;

        await _tenant.SaveChangesAsync(ct);

        // Run the matcher (new SaveChanges internally). Wrapped in try so a matcher failure
        // doesn't lose the InboundEmail → BankTransaction link we just wrote.
        try
        {
            await _matcher.MatchAsync(transaction.Id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Matcher failed for transaction {TxId} — transaction remains Unmatched", transaction.Id);
        }

        return EParseStatus.Parsed;
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    /// <summary>SHA-256 stable hash of MessageId + IMAP UID + mailbox id — email-level idempotency.</summary>
    internal static string ComputeDeduplicationHash(long mailboxId, InboundEmailPayload p)
    {
        // ServerReceivedAt is included so bounce-backs with identical MessageId but different
        // delivery dates are not collapsed.
        var input = $"{mailboxId}|{p.MessageId}|{p.ImapUid ?? string.Empty}|{p.ServerReceivedAt:O}";
        return Sha256Hex(input);
    }

    /// <summary>SHA-256 stable hash over the transaction shape — transaction-level idempotency.</summary>
    internal static string ComputeTransactionHash(long bankAccountId, BankEmailParsed p)
    {
        var input = string.Join("|",
            bankAccountId,
            p.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            p.CurrencyCode,
            p.Direction,
            p.TransactionDate.ToUniversalTime().ToString("O"),
            p.VariableSymbol ?? "",
            p.CounterpartyAccount ?? "");
        return Sha256Hex(input);
    }

    private static string Sha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static (string? Value, bool Truncated) Truncate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return (s, false);
        if (s.Length <= MaxBodyChars) return (s, false);
        return (s[..MaxBodyChars], true);
    }

    private static string TruncateRaw(string s) =>
        s.Length <= 65_536 ? s : s[..65_536];
}
