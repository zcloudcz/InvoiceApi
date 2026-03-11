using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Tax;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for tax estimation and configuration.
/// Provides endpoints to calculate income tax, social/health insurance for self-employed persons (OSVČ/SZČO).
///
/// Junior note: This controller delegates all calculations to ITaxEstimationService.
/// The service uses TaxYearConfig (annual rates stored in the master DB) — no hard-coded rates here.
/// </summary>
[ApiController]
[Route("api/tax")]
[Produces("application/json")]
[Authorize]
public class TaxController : ControllerBase
{
    private readonly ITaxEstimationService _service;
    private readonly IPdfExportService _pdfExportService;
    private readonly ILogger<TaxController> _logger;

    public TaxController(
        ITaxEstimationService service,
        IPdfExportService pdfExportService,
        ILogger<TaxController> logger)
    {
        _service = service;
        _pdfExportService = pdfExportService;
        _logger = logger;
    }

    /// <summary>
    /// Estimates tax obligations for a single regime.
    /// POST because the request body contains calculation inputs.
    /// </summary>
    [HttpPost("estimate")]
    [ProducesResponseType(typeof(TaxEstimationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Estimate(
        [FromBody] TaxEstimationRequest request, CancellationToken ct)
    {
        if (request.GrossIncome < 0)
            return BadRequest("Gross income cannot be negative.");

        if (string.IsNullOrEmpty(request.TaxRegime))
            return BadRequest("Tax regime is required.");

        try
        {
            var result = await _service.EstimateAsync(request, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Tax estimation failed: config not found");
            return BadRequest(ex.Message);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Tax estimation failed: invalid argument");
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Compares all applicable tax regimes for a given income, sorted by total obligations (best first).
    /// </summary>
    [HttpGet("compare")]
    [ProducesResponseType(typeof(List<TaxEstimationResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Compare(
        [FromQuery] decimal grossIncome,
        [FromQuery] string country = "CZ",
        [FromQuery] int year = 2026,
        [FromQuery] string? activityType = null,
        [FromQuery] bool isMainActivity = true,
        [FromQuery] decimal? actualExpenses = null,
        CancellationToken ct = default)
    {
        if (grossIncome < 0)
            return BadRequest("Gross income cannot be negative.");

        var results = await _service.CompareRegimesAsync(
            grossIncome, country, year, activityType, isMainActivity, actualExpenses, ct);

        return Ok(results);
    }

    /// <summary>
    /// Returns the tax year configuration for a specific country and year.
    /// </summary>
    [HttpGet("config/{country}/{year:int}")]
    [ProducesResponseType(typeof(TaxYearConfigDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetConfig(string country, int year, CancellationToken ct)
    {
        var config = await _service.GetConfigAsync(country, year, ct);
        return config == null ? NotFound() : Ok(config);
    }

    /// <summary>
    /// Returns all available tax year configurations.
    /// </summary>
    [HttpGet("configs")]
    [ProducesResponseType(typeof(List<TaxYearConfigDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllConfigs(CancellationToken ct)
    {
        var configs = await _service.GetAllConfigsAsync(ct);
        return Ok(configs);
    }

    /// <summary>
    /// Creates a new tax year configuration.
    /// Only SysAdmin or Admin can manage tax configs.
    /// </summary>
    [HttpPost("config")]
    [Authorize(Roles = "Admin,SysAdmin")]
    [ProducesResponseType(typeof(TaxYearConfigDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateConfig(
        [FromBody] CreateTaxYearConfigDto dto, CancellationToken ct)
    {
        try
        {
            var result = await _service.CreateConfigAsync(dto, ct);
            return CreatedAtAction(nameof(GetConfig),
                new { country = result.Country, year = result.Year }, result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Updates an existing tax year configuration by ID.
    /// </summary>
    [HttpPut("config/{id:long}")]
    [Authorize(Roles = "Admin,SysAdmin")]
    [ProducesResponseType(typeof(TaxYearConfigDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateConfig(
        long id, [FromBody] CreateTaxYearConfigDto dto, CancellationToken ct)
    {
        try
        {
            var result = await _service.UpdateConfigAsync(id, dto, ct);
            return result == null ? NotFound() : Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Deletes a tax year configuration by ID.
    /// </summary>
    [HttpDelete("config/{id:long}")]
    [Authorize(Roles = "Admin,SysAdmin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteConfig(long id, CancellationToken ct)
    {
        var deleted = await _service.DeleteConfigAsync(id, ct);
        return deleted ? NoContent() : NotFound();
    }

    /// <summary>
    /// Returns the annual gross income calculated from issued invoices for a given year.
    /// Sums TotalBeforeVat of all Completed/Paid invoices.
    /// </summary>
    [HttpGet("income/{year:int}")]
    [ProducesResponseType(typeof(AnnualIncomeDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAnnualIncome(int year, CancellationToken ct)
    {
        var result = await _service.GetAnnualIncomeAsync(year, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns upcoming insurance advance payment information.
    /// Calculates monthly social/health insurance amounts and next payment date.
    /// </summary>
    [HttpGet("insurance-advance")]
    [ProducesResponseType(typeof(InsuranceAdvanceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> GetInsuranceAdvance(CancellationToken ct)
    {
        var result = await _service.GetInsuranceAdvanceAsync(ct);
        return result == null ? NoContent() : Ok(result);
    }

    /// <summary>
    /// Exports the regime comparison results as a PDF document.
    /// Generates an HTML table from the comparison, then converts to PDF via iText7.
    /// </summary>
    [HttpGet("compare/pdf")]
    [Produces("application/pdf")]
    public async Task<IActionResult> ExportComparisonPdf(
        [FromQuery] decimal grossIncome,
        [FromQuery] string country = "CZ",
        [FromQuery] int year = 2026,
        [FromQuery] bool isMainActivity = true,
        [FromQuery] decimal? actualExpenses = null,
        CancellationToken ct = default)
    {
        if (grossIncome < 0)
            return BadRequest("Gross income cannot be negative.");

        var results = await _service.CompareRegimesAsync(
            grossIncome, country, year, null, isMainActivity, actualExpenses, ct);

        if (results.Count == 0)
            return BadRequest("No applicable regimes found.");

        // Build HTML table for PDF export.
        var currencyCode = results.First().CurrencyCode;
        var html = BuildComparisonHtml(results, grossIncome, country, year, currencyCode);

        var pdfBytes = await _pdfExportService.GeneratePdfFromHtmlAsync(html, ct);

        return File(pdfBytes, "application/pdf",
            $"tax-comparison-{country}-{year}.pdf");
    }

    /// <summary>
    /// Builds an HTML document with the regime comparison table for PDF export.
    /// </summary>
    private static string BuildComparisonHtml(
        List<TaxEstimationResult> results, decimal grossIncome,
        string country, int year, string currencyCode)
    {
        var rows = string.Join("\n", results.Select((r, i) =>
            $@"<tr{(i == 0 ? " style='background-color: #e8f5e9;'" : "")}>
                <td>{r.TaxRegime}{(i == 0 ? " ★" : "")}</td>
                <td style='text-align: right'>{r.Expenses:N0}</td>
                <td style='text-align: right'>{r.IncomeTax:N0}</td>
                <td style='text-align: right'>{r.SocialInsurance:N0}</td>
                <td style='text-align: right'>{r.HealthInsurance:N0}</td>
                <td style='text-align: right'><strong>{r.TotalObligations:N0}</strong></td>
                <td style='text-align: right; color: green;'>{r.NetIncome:N0}</td>
                <td style='text-align: right'>{r.EffectiveTaxRate}%</td>
            </tr>"));

        return $@"<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8' />
    <style>
        body {{ font-family: Arial, sans-serif; font-size: 12px; margin: 20px; }}
        h1 {{ font-size: 18px; margin-bottom: 5px; }}
        h2 {{ font-size: 14px; color: #555; margin-top: 0; }}
        table {{ border-collapse: collapse; width: 100%; margin-top: 15px; }}
        th, td {{ border: 1px solid #ddd; padding: 6px 8px; font-size: 11px; }}
        th {{ background-color: #f5f5f5; text-align: left; }}
    </style>
</head>
<body>
    <h1>Tax Regime Comparison — {country} {year}</h1>
    <h2>Gross Income: {grossIncome:N0} {currencyCode}</h2>
    <table>
        <thead>
            <tr>
                <th>Regime</th>
                <th style='text-align: right'>Expenses</th>
                <th style='text-align: right'>Income Tax</th>
                <th style='text-align: right'>Social Ins.</th>
                <th style='text-align: right'>Health Ins.</th>
                <th style='text-align: right'>Total</th>
                <th style='text-align: right'>Net Income</th>
                <th style='text-align: right'>Eff. Rate</th>
            </tr>
        </thead>
        <tbody>
            {rows}
        </tbody>
    </table>
    <p style='margin-top: 15px; color: #888; font-size: 10px;'>
        Generated by Fakvio on {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC.
        ★ = Best option (lowest total obligations).
    </p>
</body>
</html>";
    }
}
