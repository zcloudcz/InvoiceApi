using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Readiness;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Read-only endpoint that answers "can this tenant issue documents yet?".
///
/// It is a thin wrapper over <see cref="ITenantReadinessService"/> — all rules live there
/// (see DEVGUIDE §4.12), so the settings banner, the invoice gate and the chat assistant
/// cannot drift apart.
///
/// Authorization: any authenticated user of the tenant. Deliberately NOT SysAdmin-only —
/// the person who has to fix the missing settings is the ordinary user staring at the
/// readiness banner in the UI.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize]
public class ReadinessController : ControllerBase
{
    private readonly ITenantReadinessService _readinessService;
    private readonly ILogger<ReadinessController> _logger;

    public ReadinessController(
        ITenantReadinessService readinessService,
        ILogger<ReadinessController> logger)
    {
        _readinessService = readinessService;
        _logger = logger;
    }

    /// <summary>
    /// Returns everything the tenant still has to fill in before it can invoice safely.
    ///
    /// Without <paramref name="issuerId"/> every issuer of the tenant is checked; with it,
    /// only that one. An empty <c>issues</c> array (and <c>isReady: true</c>) means there is
    /// nothing to fix.
    /// </summary>
    /// <param name="issuerId">Optional — check only this issuer instead of all of them.</param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    /// <response code="200">Readiness report (possibly with issues — that is not an error).</response>
    /// <response code="404">An explicit <paramref name="issuerId"/> that does not exist in this tenant.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ReadinessReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReadinessReportDto>> GetReport(
        [FromQuery] long? issuerId = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/readiness — IssuerId: {IssuerId}", issuerId);

        var report = await _readinessService.GetReportAsync(issuerId, cancellationToken);

        // The service reports "there is no issuer to invoice with" as ISSUER_MISSING whether
        // the tenant has no issuer at all or the caller asked for an ID that does not exist —
        // by design, because the service cannot tell the two apart, but the caller can: it
        // supplied the ID. So an ISSUER_MISSING answer to an explicit ID means "that issuer
        // is not here" → 404. Without an ID it means "your tenant is empty" → 200 + report.
        if (issuerId.HasValue && report.Issues.Any(i => i.Code == ReadinessCodes.IssuerMissing))
        {
            _logger.LogWarning("GET /api/readiness — issuer {IssuerId} not found in this tenant", issuerId);
            return NotFound(new { message = $"Issuer with ID {issuerId} not found" });
        }

        return Ok(report);
    }
}
