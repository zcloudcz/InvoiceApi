using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.PaymentMatching;

namespace Fakvio.Application.Service;

/// <summary>
/// Read-side service for the Payments grid — translates filter DTOs into EF Core
/// queries and maps the result into lightweight grid rows.
/// </summary>
public interface IBankTransactionQueryService
{
    /// <summary>Server-side paginated list for the Payments grid.</summary>
    Task<PagedResult<BankTransactionDto>> ListAsync(
        BankTransactionFilterDto filter,
        PaginationParams paging,
        CancellationToken ct = default);

    /// <summary>Single row with full details (including matched invoices).</summary>
    Task<BankTransactionDto?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>Count of transactions currently in "Unmatched" — for the dashboard badge.</summary>
    Task<int> GetUnmatchedCountAsync(CancellationToken ct = default);
}
