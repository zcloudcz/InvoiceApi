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
    /// Optional company (issuer) ID to filter dashboard data by.
    /// When provided, only invoices belonging to this issuer are counted.
    /// Null = show all invoices (for SysAdmin without impersonation).
    /// </param>
    /// <param name="ct">Cancellation token for async operation.</param>
    /// <returns>DashboardDto containing all aggregated statistics.</returns>
    Task<DashboardDto> GetDashboardAsync(long? companyId = null, CancellationToken ct = default);
}
