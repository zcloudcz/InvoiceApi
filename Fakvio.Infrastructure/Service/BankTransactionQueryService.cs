using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.PaymentMatching;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Default <see cref="IBankTransactionQueryService"/>. Uses AsNoTracking for
/// read-only queries to keep change-tracking overhead off the grid's hot path.
/// </summary>
public class BankTransactionQueryService : IBankTransactionQueryService
{
    private readonly TenantDbContext _context;
    private readonly ILogger<BankTransactionQueryService> _logger;

    public BankTransactionQueryService(
        TenantDbContext context,
        ILogger<BankTransactionQueryService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PagedResult<BankTransactionDto>> ListAsync(
        BankTransactionFilterDto filter,
        PaginationParams paging,
        CancellationToken ct = default)
    {
        var query = BuildFilteredQuery(filter);

        var total = await query.CountAsync(ct);

        // Default ordering newest first. Grid headers can override via SortBy later —
        // for MVP we keep it simple to avoid a second code path.
        var page = paging.Page <= 0 ? 1 : paging.Page;
        var pageSize = paging.PageSize <= 0 ? 50 : paging.PageSize;

        var rows = await query
            .OrderByDescending(t => t.TransactionDate)
            .ThenByDescending(t => t.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(t => t.BankAccount)
            .Include(t => t.PaymentMatch)
                .ThenInclude(m => m.Invoice)
            .Include(t => t.RecognizedCounterparty)
            .AsNoTracking()
            .ToListAsync(ct);

        var items = rows.Select(MapToDto).ToList();
        return new PagedResult<BankTransactionDto>(items, total, page, pageSize);
    }

    /// <inheritdoc />
    public async Task<BankTransactionDto?> GetAsync(long id, CancellationToken ct = default)
    {
        var row = await _context.BankTransaction
            .AsNoTracking()
            .Include(t => t.BankAccount)
            .Include(t => t.PaymentMatch)
                .ThenInclude(m => m.Invoice)
            .Include(t => t.RecognizedCounterparty)
            .FirstOrDefaultAsync(t => t.Id == id, ct);

        return row == null ? null : MapToDto(row);
    }

    /// <inheritdoc />
    public async Task<int> GetUnmatchedCountAsync(CancellationToken ct = default)
    {
        // Both Unmatched and NeedsReview count toward "needs user attention".
        return await _context.BankTransaction
            .AsNoTracking()
            .CountAsync(t => t.MatchStatus == EMatchStatus.Unmatched
                        || t.MatchStatus == EMatchStatus.NeedsReview, ct);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private IQueryable<BankTransaction> BuildFilteredQuery(BankTransactionFilterDto f)
    {
        var q = _context.BankTransaction.AsQueryable();

        if (f.Status.HasValue) q = q.Where(t => t.MatchStatus == f.Status.Value);
        if (f.Direction.HasValue) q = q.Where(t => t.Direction == f.Direction.Value);
        if (f.BankAccountId.HasValue) q = q.Where(t => t.BankAccountId == f.BankAccountId.Value);
        if (f.From.HasValue) q = q.Where(t => t.TransactionDate >= f.From.Value);
        if (f.To.HasValue) q = q.Where(t => t.TransactionDate <= f.To.Value);

        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var s = f.Search.Trim();
            q = q.Where(t =>
                (t.CounterpartyName != null && EF.Functions.ILike(t.CounterpartyName, $"%{s}%"))
                || (t.Message != null && EF.Functions.ILike(t.Message, $"%{s}%"))
                || (t.VariableSymbol != null && t.VariableSymbol.Contains(s))
                || (t.CounterpartyAccount != null && t.CounterpartyAccount.Contains(s)));
        }

        return q;
    }

    private static BankTransactionDto MapToDto(BankTransaction t)
    {
        return new BankTransactionDto
        {
            Id = t.Id,
            BankAccountId = t.BankAccountId,
            BankAccountLabel = t.BankAccount?.Label ?? t.BankAccount?.AccountNumber,
            TransactionDate = t.TransactionDate,
            Amount = t.Amount,
            CurrencyCode = t.CurrencyCode,
            Direction = t.Direction,
            VariableSymbol = t.VariableSymbol,
            ConstantSymbol = t.ConstantSymbol,
            SpecificSymbol = t.SpecificSymbol,
            CounterpartyAccount = t.CounterpartyAccount,
            CounterpartyName = t.CounterpartyName,
            Message = t.Message,
            ImportSource = t.ImportSource,
            MatchStatus = t.MatchStatus,
            ParserConfidence = t.ParserConfidence,
            ParserModel = t.ParserModel,
            MatchedInvoiceNumbers = t.PaymentMatch
                .Where(m => m.Invoice != null)
                .Select(m => m.Invoice!.DocumentNumber ?? "")
                .ToList(),
            MatchedTotal = t.PaymentMatch.Sum(m => m.MatchedAmount),
            RecognizedCounterpartyId = t.RecognizedCounterpartyId,
            RecognizedCounterpartyLabel = t.RecognizedCounterparty?.Label,
            RecognizedCategory = t.RecognizedCounterparty?.Category,
        };
    }
}
