using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.PaymentMatching;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Default <see cref="IBankStatementImportService"/>. Runs inside an HTTP request, so the
/// DI-scoped TenantDbContext already points to the caller's tenant schema.
///
/// Flow per statement: resolve target BankAccount -> build BankTransaction rows (skipping
/// storno items and already-imported ones by DeduplicationHash) -> save -> run
/// <see cref="IPaymentMatchingService.MatchAsync"/> on the new incoming credits
/// (same path as the IMAP flow, incl. auto-DPP and webhooks).
/// </summary>
public class BankStatementImportService : IBankStatementImportService
{
    private readonly TenantDbContext _tenant;
    private readonly IPaymentMatchingService _matcher;
    private readonly ILogger<BankStatementImportService> _logger;

    public BankStatementImportService(
        TenantDbContext tenant,
        IPaymentMatchingService matcher,
        ILogger<BankStatementImportService> logger)
    {
        _tenant = tenant;
        _matcher = matcher;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<BankStatementImportResultDto> ImportAsync(byte[] content, long? bankAccountId, CancellationToken ct = default)
    {
        var parsed = GpcParser.Parse(content);
        var result = new BankStatementImportResultDto
        {
            Statements = parsed.Statements.Count,
            Errors = parsed.Errors,
        };

        // Only accounts of the issuer (our own accounts), never client accounts.
        var accounts = await _tenant.BankAccount
            .Where(b => b.Client.IsIssuer)
            .ToListAsync(ct);

        foreach (var statement in parsed.Statements)
        {
            var account = bankAccountId.HasValue
                ? accounts.FirstOrDefault(a => a.Id == bankAccountId.Value)
                : accounts.FirstOrDefault(a => AccountMatches(a.AccountNumber, statement.Account));
            if (account == null)
            {
                result.Errors.Add($"Statement {statement.SequenceNumber} (account {FormatOwnAccount(statement.Account)}): no matching bank account in company settings - add it there or choose the target account.");
                continue;
            }

            await ImportStatementAsync(statement, account, result, ct);
        }

        return result;
    }

    private async Task ImportStatementAsync(GpcStatement statement, BankAccount account, BankStatementImportResultDto result, CancellationToken ct)
    {
        var occurrences = new Dictionary<string, int>();
        var candidates = new List<BankTransaction>();

        foreach (var item in statement.Items)
        {
            if (item.PostingCode is 4 or 5)
            {
                result.Errors.Add($"Line {item.LineNumber}: storno item skipped (reverse it manually if needed).");
                continue;
            }

            var direction = item.PostingCode == 2 ? EPaymentDirection.Incoming : EPaymentDirection.Outgoing;
            var key = string.Join("|", account.Id,
                item.ValueDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                item.Amount.ToString(CultureInfo.InvariantCulture), direction,
                item.VariableSymbol, item.ConstantSymbol, item.SpecificSymbol, item.CounterAccount, item.DocumentNumber);
            // Identical lines in one statement are distinct payments - number them so re-import stays stable.
            occurrences[key] = occurrences.GetValueOrDefault(key) + 1;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"gpc|{key}|{occurrences[key]}"))).ToLowerInvariant();

            candidates.Add(new BankTransaction
            {
                BankAccountId = account.Id,
                DeduplicationHash = hash,
                TransactionDate = item.ValueDate,
                Amount = item.Amount,
                CurrencyCode = account.CurrencyCode ?? "CZK",
                Direction = direction,
                VariableSymbol = item.VariableSymbol,
                ConstantSymbol = item.ConstantSymbol,
                SpecificSymbol = item.SpecificSymbol,
                CounterpartyAccount = item.CounterAccount,
                CounterpartyName = item.Name,
                TransactionCode = item.DocumentNumber,
                ImportSource = EImportSource.GpcImport,
                MatchStatus = EMatchStatus.Unmatched,
                RawPayload = item.RawLine,
            });
        }

        var hashes = candidates.Select(c => c.DeduplicationHash).ToList();
        var existing = (await _tenant.BankTransaction
            .Where(t => t.BankAccountId == account.Id && hashes.Contains(t.DeduplicationHash))
            .Select(t => t.DeduplicationHash)
            .ToListAsync(ct)).ToHashSet();

        var fresh = candidates.Where(c => !existing.Contains(c.DeduplicationHash)).ToList();
        result.Duplicates += candidates.Count - fresh.Count;
        if (fresh.Count == 0) return;

        _tenant.BankTransaction.AddRange(fresh);
        await _tenant.SaveChangesAsync(ct);
        result.Imported += fresh.Count;

        // Only incoming credits are matched against issued invoices.
        foreach (var tx in fresh.Where(t => t.Direction == EPaymentDirection.Incoming))
        {
            try
            {
                await _matcher.MatchAsync(tx.Id, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Matcher failed for imported transaction {TxId} - it remains Unmatched", tx.Id);
            }

            // MatchAsync updates the same tracked entity; re-read status from the DB-tracked instance.
            if (tx.MatchStatus is EMatchStatus.Matched or EMatchStatus.PartiallyMatched) result.Matched++;
            else result.Unmatched++;
        }
    }

    /// <summary>"123-4567890/0800" (our account) vs GPC's zero-padded prefix+number; bank code is not in the 074 record.</summary>
    internal static bool AccountMatches(string accountNumber, string gpcAccount16)
    {
        if (gpcAccount16.Length != 16) return false;
        var local = accountNumber.Split('/')[0].Trim();
        var parts = local.Split('-');
        var prefix = parts.Length == 2 ? parts[0] : "";
        var number = parts.Length == 2 ? parts[1] : parts[0];
        return prefix.TrimStart('0') == gpcAccount16[..6].TrimStart('0')
            && number.TrimStart('0') == gpcAccount16[6..].TrimStart('0');
    }

    private static string FormatOwnAccount(string account16)
    {
        var prefix = account16[..6].TrimStart('0');
        var number = account16[6..].TrimStart('0');
        return prefix.Length > 0 ? $"{prefix}-{number}" : number;
    }
}
