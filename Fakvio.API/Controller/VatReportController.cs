using Fakvio.Application.Exceptions;
using Fakvio.Contracts.Dto.VatReport;
using Fakvio.Application.Service;
using Fakvio.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for VAT (DPH) reporting.
/// Provides aggregated VAT data for a given period: output VAT vs. input VAT,
/// tax liability, revenue, expenses, and profit.
///
/// Also exposes EPO export endpoints for DPHDP3 (VAT return) and DPHKH1 (control statement).
/// EPO endpoints require the company to be a VAT payer and the tenant's CompanySystemSettings
/// to have all required EPO header fields configured (c_ufo, c_pracufo).
/// </summary>
[ApiController]
[Route("api/vat-report")]
[Produces("application/json")]
[Authorize]
public class VatReportController : ControllerBase
{
    private readonly IVatReportService _service;
    private readonly ILogger<VatReportController> _logger;

    public VatReportController(IVatReportService service, ILogger<VatReportController> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <summary>
    /// Generates a VAT report for the specified period.
    /// Uses TaxableSupplyDate (DUZP) as the date criterion per Czech VAT law.
    /// </summary>
    /// <param name="from">Period start date (inclusive, yyyy-MM-dd).</param>
    /// <param name="to">Period end date (inclusive, yyyy-MM-dd).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>VatReportDto with full breakdown by VAT rate.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(VatReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<VatReportDto>> GetReport(
        [FromQuery] DateTime from,
        [FromQuery] DateTime to,
        CancellationToken ct = default)
    {
        if (from > to)
            return BadRequest(new { message = "Period 'from' date must be before or equal to 'to' date." });

        var result = await _service.GetReportAsync(from, to, ct);
        return Ok(result);
    }

    // =========================================================================
    // EPO Export Endpoints
    // =========================================================================

    /// <summary>
    /// Downloads an EPO DPHDP3 XML export (Czech VAT return — "Přiznání k dani z přidané hodnoty")
    /// for the given year and period.
    ///
    /// The response body is UTF-8 XML (no BOM) validated against the official MFČR XSD.
    /// Content-Disposition header includes the file name in the format DPHDP3_{year}_M{mm}.xml
    /// (monthly) or DPHDP3_{year}_Q{q}.xml (quarterly).
    ///
    /// Returns 400 with code EPO_HEADER_INCOMPLETE when CompanySystemSettings are missing
    /// required EPO fields (c_ufo, c_pracufo). The response body lists the missing fields
    /// so the UI can navigate the user to the settings screen (issue #6).
    ///
    /// Returns 403 with code VAT_PAYER_REQUIRED when the current company is not a VAT payer.
    /// </summary>
    /// <param name="year">Tax year (e.g. 2026). Must be in [2024, currentYear+1].</param>
    /// <param name="period">
    /// Period number: 1–12 for Monthly, 1–4 for Quarterly.
    /// </param>
    /// <param name="type">Monthly or Quarterly.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>application/xml file attachment.</returns>
    [HttpGet("epo/return")]
    // Success = application/xml; error responses = application/json — both must be accepted.
    [Produces("application/xml", "application/json")]
    [ProducesResponseType(typeof(byte[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DownloadEpoVatReturn(
        [FromQuery] int year,
        [FromQuery] int period,
        [FromQuery] EVatPeriodType type,
        [FromQuery] string[]? goods,
        CancellationToken ct = default)
    {
        try
        {
            var bytes = await _service.ExportEpoVatReturnAsync(year, period, type, ct, goods);
            var filename = BuildEpoFilename("DPHDP3", year, period, type);

            _logger.LogInformation(
                "EPO DPHDP3 downloaded: year={Year}, period={Period}, type={Type}, file={File}, {Bytes} bytes",
                year, period, type, filename, bytes.Length);

            return File(bytes, "application/xml", filename);
        }
        catch (VatPayerRequiredException ex)
        {
            // The issuer is not a VAT payer — EPO filings are not applicable.
            _logger.LogWarning("EPO DPHDP3 export blocked: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code    = "VAT_PAYER_REQUIRED",
                message = ex.Message
            });
        }
        catch (EpoHeaderIncompleteException ex)
        {
            // Required EPO header fields missing — tell the UI which ones so it can guide the user.
            _logger.LogWarning(
                "EPO DPHDP3 export failed — incomplete header: {MissingFields}",
                string.Join(", ", ex.MissingFields));

            return BadRequest(new
            {
                code           = "EPO_HEADER_INCOMPLETE",
                message        = ex.Message,
                missingFields  = ex.MissingFields
            });
        }
        catch (ArgumentOutOfRangeException ex)
        {
            _logger.LogWarning("EPO DPHDP3 export: invalid argument — {Message}", ex.Message);
            return BadRequest(new { code = "INVALID_ARGUMENT", message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // Issuer not found or company settings missing.
            _logger.LogWarning("EPO DPHDP3 export failed: {Message}", ex.Message);
            return BadRequest(new { code = "CONFIGURATION_ERROR", message = ex.Message });
        }
    }

    /// <summary>
    /// Downloads an EPO DPHKH1 XML export (Czech VAT control statement —
    /// "Kontrolní hlášení DPH") for the given year and period.
    ///
    /// The response body is UTF-8 XML (no BOM) validated against the official MFČR XSD.
    /// Content-Disposition header includes the file name in the format DPHKH1_{year}_M{mm}.xml
    /// (monthly) or DPHKH1_{year}_Q{q}.xml (quarterly).
    ///
    /// Returns 400 with code EPO_HEADER_INCOMPLETE when CompanySystemSettings are missing
    /// required EPO fields (c_ufo, c_pracufo).
    /// </summary>
    /// <param name="year">Tax year (e.g. 2026). Must be in [2024, currentYear+1].</param>
    /// <param name="period">Period number: 1–12 for Monthly, 1–4 for Quarterly.</param>
    /// <param name="type">Monthly or Quarterly.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>application/xml file attachment.</returns>
    [HttpGet("epo/control-statement")]
    // Success = application/xml; error responses = application/json — both must be accepted.
    [Produces("application/xml", "application/json")]
    [ProducesResponseType(typeof(byte[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DownloadEpoControlStatement(
        [FromQuery] int year,
        [FromQuery] int period,
        [FromQuery] EVatPeriodType type,
        CancellationToken ct = default)
    {
        try
        {
            var bytes = await _service.ExportEpoControlStatementAsync(year, period, type, ct);
            var filename = BuildEpoFilename("DPHKH1", year, period, type);

            _logger.LogInformation(
                "EPO DPHKH1 downloaded: year={Year}, period={Period}, type={Type}, file={File}, {Bytes} bytes",
                year, period, type, filename, bytes.Length);

            return File(bytes, "application/xml", filename);
        }
        catch (VatPayerRequiredException ex)
        {
            // The issuer is not a VAT payer — EPO filings are not applicable.
            _logger.LogWarning("EPO DPHKH1 export blocked: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code    = "VAT_PAYER_REQUIRED",
                message = ex.Message
            });
        }
        catch (EpoHeaderIncompleteException ex)
        {
            _logger.LogWarning(
                "EPO DPHKH1 export failed — incomplete header: {MissingFields}",
                string.Join(", ", ex.MissingFields));

            return BadRequest(new
            {
                code           = "EPO_HEADER_INCOMPLETE",
                message        = ex.Message,
                missingFields  = ex.MissingFields
            });
        }
        catch (ArgumentOutOfRangeException ex)
        {
            _logger.LogWarning("EPO DPHKH1 export: invalid argument — {Message}", ex.Message);
            return BadRequest(new { code = "INVALID_ARGUMENT", message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("EPO DPHKH1 export failed: {Message}", ex.Message);
            return BadRequest(new { code = "CONFIGURATION_ERROR", message = ex.Message });
        }
    }

    /// <summary>
    /// Previews the DPHSHV (EU summary statement) rows: issued invoices to EU (non-CZ)
    /// clients with a VAT id, aggregated by (country, VAT id, supply code).
    /// </summary>
    /// <param name="year">Tax year. Must be in [2024, currentYear+1].</param>
    /// <param name="period">1–12 for Monthly, 1–4 for Quarterly.</param>
    /// <param name="type">Monthly or Quarterly.</param>
    /// <param name="goods">
    /// Customers to report as GOODS (supply code 0) instead of the default SERVICES (3),
    /// as country prefix + VAT id (e.g. "DE123456789"). Repeat the parameter per customer.
    /// </param>
    [HttpGet("epo/summary-statement/preview")]
    [ProducesResponseType(typeof(List<SummaryStatementRowDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PreviewEpoSummaryStatement(
        [FromQuery] int year,
        [FromQuery] int period,
        [FromQuery] EVatPeriodType type,
        [FromQuery] string[]? goods,
        CancellationToken ct = default)
    {
        try
        {
            return Ok(await _service.GetSummaryStatementRowsAsync(year, period, type, goods, ct));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new { code = "INVALID_ARGUMENT", message = ex.Message });
        }
    }

    /// <summary>
    /// Downloads an EPO DPHSHV XML export (EU VAT summary statement — "Souhrnné hlášení")
    /// validated against the official MFČR XSD. File name: DPHSHV_{year}_M{mm}.xml / _Q{q}.xml.
    /// Same error contract as the other EPO exports (403 VAT_PAYER_REQUIRED,
    /// 400 EPO_HEADER_INCOMPLETE / INVALID_ARGUMENT / CONFIGURATION_ERROR — the last one
    /// also when there is nothing to report).
    /// </summary>
    /// <param name="goods">See <see cref="PreviewEpoSummaryStatement"/>.</param>
    [HttpGet("epo/summary-statement")]
    [Produces("application/xml", "application/json")]
    [ProducesResponseType(typeof(byte[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DownloadEpoSummaryStatement(
        [FromQuery] int year,
        [FromQuery] int period,
        [FromQuery] EVatPeriodType type,
        [FromQuery] string[]? goods,
        CancellationToken ct = default)
    {
        try
        {
            var bytes = await _service.ExportEpoSummaryStatementAsync(year, period, type, goods, ct);
            return File(bytes, "application/xml", BuildEpoFilename("DPHSHV", year, period, type));
        }
        catch (VatPayerRequiredException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { code = "VAT_PAYER_REQUIRED", message = ex.Message });
        }
        catch (EpoHeaderIncompleteException ex)
        {
            return BadRequest(new { code = "EPO_HEADER_INCOMPLETE", message = ex.Message, missingFields = ex.MissingFields });
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new { code = "INVALID_ARGUMENT", message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { code = "CONFIGURATION_ERROR", message = ex.Message });
        }
    }

    // =========================================================================
    // Private helpers
    // =========================================================================

    /// <summary>
    /// Builds the standard EPO file name used in Content-Disposition.
    /// Format: {FormType}_{year}_M{mm:D2}.xml  (monthly)
    ///         {FormType}_{year}_Q{q}.xml       (quarterly)
    ///
    /// Examples:
    ///   DPHDP3_2026_M04.xml  (Monthly, period 4)
    ///   DPHKH1_2026_Q1.xml   (Quarterly, period 1)
    /// </summary>
    private static string BuildEpoFilename(string formType, int year, int period, EVatPeriodType type)
    {
        // Monthly periods use two-digit zero-padded numbers (M01–M12).
        // Quarterly periods use single-digit numbers (Q1–Q4).
        var periodPart = type == EVatPeriodType.Monthly
            ? $"M{period:D2}"
            : $"Q{period}";

        return $"{formType}_{year}_{periodPart}.xml";
    }
}
