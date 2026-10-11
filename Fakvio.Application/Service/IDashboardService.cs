using Fakvio.Contracts.Dto.Dashboard;

namespace Fakvio.Application.Service;

/// <summary>
/// Service interface for aggregating dashboard statistics.
/// Provides a single method to get all dashboard data in one call,
/// which is more efficient than making multiple separate API requests.
/// </summary>
public interface IDashboardService
{
    /// <summary>
    /// Retrieves all dashboard statistics: invoice counts, client counts,
    /// unpaid totals, VAT rate counts, recent invoices, and overdue invoices.
    /// </summary>
    /// <param name="companyId">
    /// Company ID of the caller, used for logging only. It is NOT a filter: the tenant schema already
    /// holds just this company's documents, and a company id (master DB) is not an issuer client id.
    /// </param>
    /// <param name="ct">Cancellation token for async operation.</param>
    /// <returns>DashboardDto containing all aggregated statistics.</returns>
    Task<DashboardDto> GetDashboardAsync(long? companyId = null, CancellationToken ct = default);
}
