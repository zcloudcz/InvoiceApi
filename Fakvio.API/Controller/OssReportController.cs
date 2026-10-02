using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.OssReport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Quarterly EU OSS (One-Stop-Shop) report for the current tenant — see DEVGUIDE §4.16.
/// Tenant-scoped (standard tenant resolution), any authenticated tenant user may read it
/// (same as /api/vat-report).
/// </summary>
[ApiController]
[Route("api/oss-report")]
[Produces("application/json")]
[Authorize]
public class OssReportController : ControllerBase
{
    private readonly IOssReportService _service;

    public OssReportController(IOssReportService service) => _service = service;

    /// <summary>Base and VAT per destination country and rate in EUR for a calendar quarter.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(OssReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Get([FromQuery] int year, [FromQuery] int quarter, CancellationToken ct = default)
    {
        try
        {
            return Ok(await _service.GetReportAsync(year, quarter, ct));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new { code = "INVALID_ARGUMENT", message = ex.Message });
        }
        catch (EcbRateUnavailableException ex)
        {
            // Upstream (ECB) problem — say so clearly so the user can retry later.
            return StatusCode(StatusCodes.Status502BadGateway, new { code = "ECB_RATE_UNAVAILABLE", message = ex.Message });
        }
    }

    /// <summary>The same report as a CSV file (OSS_{year}_Q{q}.csv).</summary>
    [HttpGet("csv")]
    [Produces("text/csv", "application/json")]
    [ProducesResponseType(typeof(byte[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetCsv([FromQuery] int year, [FromQuery] int quarter, CancellationToken ct = default)
    {
        try
        {
            var report = await _service.GetReportAsync(year, quarter, ct);
            return File(_service.ToCsv(report), "text/csv", $"OSS_{year}_Q{quarter}.csv");
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new { code = "INVALID_ARGUMENT", message = ex.Message });
        }
        catch (EcbRateUnavailableException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { code = "ECB_RATE_UNAVAILABLE", message = ex.Message });
        }
    }
}
