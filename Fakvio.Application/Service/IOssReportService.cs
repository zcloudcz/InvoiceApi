using Fakvio.Contracts.Dto.OssReport;

namespace Fakvio.Application.Service;

/// <summary>
/// Builds the quarterly EU OSS report (tenant-scoped). Only invoices with a non-null
/// <c>OssCountryCode</c> are included; the CZ DPHDP3/KH reports exclude exactly those.
/// </summary>
public interface IOssReportService
{
    /// <summary>
    /// Aggregates OSS invoices whose DUZP falls into the given calendar quarter, per country and rate,
    /// converted to EUR at the ECB rate of the last day of the quarter.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Quarter not 1–4 or year out of range.</exception>
    /// <exception cref="EcbRateUnavailableException">A needed ECB rate could not be fetched.</exception>
    Task<OssReportDto> GetReportAsync(int year, int quarter, CancellationToken ct = default);

    /// <summary>Renders the report as a UTF-8 CSV (semicolon-separated, decimal point, with BOM for Excel).</summary>
    byte[] ToCsv(OssReportDto report);
}
