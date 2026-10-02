using Fakvio.Contracts.Dto.VatReport;
using Fakvio.Domain.Enums;

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

    /// <summary>
    /// Generates an EPO DPHDP3 XML export (Czech VAT return — "Přiznání k dani z přidané hodnoty")
    /// for the given tax year and period.
    ///
    /// The returned bytes are UTF-8 XML (no BOM) that passes XSD validation against the
    /// official Czech Financial Administration schema for <paramref name="year"/>.
    ///
    /// Invoice filtering follows the same rules as <see cref="GetReportAsync"/>:
    /// Draft, Deleted, Rejected, and Received invoices are excluded.
    ///
    /// Non-CZK invoices are converted to CZK using <c>ICurrencyService.ConvertToCzkAsync</c>
    /// with the invoice's TaxableSupplyDate (DUZP) as the rate date.
    ///
    /// Throws <see cref="Fakvio.Application.Exceptions.EpoValidationException"/> if the
    /// generated XML fails XSD validation (indicates a mapping bug, not user error).
    /// </summary>
    /// <param name="year">Tax year, e.g. 2026. Must be in [2024, currentYear+1].</param>
    /// <param name="period">
    /// Period number: 1–12 for <see cref="EVatPeriodType.Monthly"/>,
    /// 1–4 for <see cref="EVatPeriodType.Quarterly"/>.
    /// </param>
    /// <param name="type">Monthly or Quarterly — determines which XSD attribute to populate
    /// and how to compute the date range for invoice filtering.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>UTF-8 XML bytes ready to upload to the EPO portal.</returns>
    Task<byte[]> ExportEpoVatReturnAsync(
        int year,
        int period,
        EVatPeriodType type,
        CancellationToken ct = default);

    /// <summary>
    /// Generates an EPO DPHKH1 XML export (Czech VAT control statement —
    /// "Kontrolní hlášení DPH") for the given tax year and period.
    ///
    /// The returned bytes are UTF-8 XML (no BOM) that passes XSD validation against the
    /// official Czech Financial Administration schema for <paramref name="year"/>.
    ///
    /// Invoice filtering follows the same rules as <see cref="GetReportAsync"/>:
    /// Draft, Deleted, and Rejected invoices are excluded.
    ///
    /// Classification rules for issued invoices:
    /// - <b>A.4</b>: total incl. VAT ≥ 10 000 CZK AND client has a CZ VAT number
    ///   (TaxNumber matches <c>CZ\d+</c>) → one row per invoice.
    /// - <b>A.5</b>: all other issued invoices (below threshold, foreign VAT number,
    ///   or no VAT number at all) → one aggregated row per period.
    ///
    /// Classification rules for received invoices:
    /// - <b>B.2</b>: total incl. VAT ≥ 10 000 CZK AND supplier has a CZ VAT number → one row per invoice.
    /// - <b>B.3</b>: all other received invoices → one aggregated row per period.
    ///
    /// A.1 (reverse charge supplied) and B.1 (reverse charge received) are filled from items
    /// with VatRegime = ReverseCharge, one row per (document, kód předmětu plnění).
    ///
    /// Non-CZK invoices are converted to CZK using <c>ICurrencyService.ConvertToCzkAsync</c>
    /// with the invoice's TaxableSupplyDate (DUZP) as the rate date.
    ///
    /// Throws <see cref="Fakvio.Application.Exceptions.EpoValidationException"/> if the
    /// generated XML fails XSD validation (indicates a mapping bug, not user error).
    /// </summary>
    /// <param name="year">Tax year, e.g. 2026. Must be in [2024, currentYear+1].</param>
    /// <param name="period">
    /// Period number: 1–12 for <see cref="EVatPeriodType.Monthly"/>,
    /// 1–4 for <see cref="EVatPeriodType.Quarterly"/>.
    /// </param>
    /// <param name="type">Monthly or Quarterly — determines XSD attribute and date range.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>UTF-8 XML bytes ready to upload to the EPO portal.</returns>
    Task<byte[]> ExportEpoControlStatementAsync(
        int year,
        int period,
        EVatPeriodType type,
        CancellationToken ct = default);

    /// <summary>
    /// Previews the DPHSHV (EU summary statement) rows for a period: issued invoices to
    /// EU (non-CZ) clients with a VAT id, aggregated by (country, VAT id, supply code).
    /// Supply code defaults to 3 (services); keys listed in <paramref name="goodsKeys"/>
    /// (format "DE123456789" = country prefix + VAT id) are reported as 0 (goods).
    /// </summary>
    Task<List<SummaryStatementRowDto>> GetSummaryStatementRowsAsync(
        int year, int period, EVatPeriodType type,
        IReadOnlyCollection<string>? goodsKeys = null,
        CancellationToken ct = default);

    /// <summary>
    /// Generates the DPHSHV XML (XSD-validated, UTF-8 no BOM) from the same rows as
    /// <see cref="GetSummaryStatementRowsAsync"/>. Throws when there is nothing to report.
    /// </summary>
    Task<byte[]> ExportEpoSummaryStatementAsync(
        int year, int period, EVatPeriodType type,
        IReadOnlyCollection<string>? goodsKeys = null,
        CancellationToken ct = default);
}
