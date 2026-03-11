using Fakvio.Contracts.Dto.VatReport;

namespace Fakvio.Application.Service;

/// <summary>
/// Service for generating VAT reports.
/// Aggregates output VAT (from issued invoices) and input VAT (from received invoices)
/// for a given period, calculates tax liability or refund.
/// </summary>
public interface IVatReportService
{
    /// <summary>
    /// Generates a VAT report for the specified period.
    /// Includes output VAT, input VAT, tax liability, revenue, expenses, and profit.
    /// </summary>
    /// <param name="from">Period start date (inclusive). Uses TaxableSupplyDate (DUZP).</param>
    /// <param name="to">Period end date (inclusive).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>VatReportDto with full breakdown by VAT rate.</returns>
    Task<VatReportDto> GetReportAsync(DateTime from, DateTime to, CancellationToken ct = default);
}
