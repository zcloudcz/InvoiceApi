using Fakvio.Contracts.Dto.VatReport;
using Fakvio.Application.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for VAT (DPH) reporting.
/// Provides aggregated VAT data for a given period: output VAT vs. input VAT,
/// tax liability, revenue, expenses, and profit.
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
}
