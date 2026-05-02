using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Alert;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for tenant-scoped business alerts.
///
/// Provides endpoints for:
///   - GET /api/alert         — list alerts (with optional unresolvedOnly filter)
///   - GET /api/alert/dashboard — summary tile data (count + 5 recent)
///   - POST /api/alert/{id}/resolve — mark an alert as resolved
///
/// All endpoints require authentication. Tenant isolation is automatic via JWT CompanyId,
/// which is used by ITenantDbContextFactory to scope TenantDbContext to the correct schema.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize]
public class AlertController : ControllerBase
{
    private readonly IAlertService _alertService;
    private readonly ICurrentUserService _currentUserService;
    private readonly ILogger<AlertController> _logger;

    public AlertController(
        IAlertService alertService,
        ICurrentUserService currentUserService,
        ILogger<AlertController> logger)
    {
        _alertService = alertService;
        _currentUserService = currentUserService;
        _logger = logger;
    }

    /// <summary>
    /// Returns alerts for the current tenant.
    /// Pass <c>unresolvedOnly=false</c> to include already-resolved alerts.
    /// Default is <c>true</c> (open alerts only).
    /// </summary>
    /// <param name="unresolvedOnly">When true (default), only unresolved alerts are returned.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of alert DTOs, newest first.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AlertDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AlertDto>>> GetAlerts(
        [FromQuery] bool unresolvedOnly = true,
        CancellationToken ct = default)
    {
        _logger.LogInformation("GET /api/alert unresolvedOnly={UnresolvedOnly}", unresolvedOnly);
        var alerts = await _alertService.GetAsync(unresolvedOnly, ct);
        return Ok(alerts);
    }

    /// <summary>
    /// Returns the dashboard summary: total open count + up to 5 most recent open alerts.
    /// Used by the "Upozornění" tile on the dashboard.
    /// </summary>
    [HttpGet("dashboard")]
    [ProducesResponseType(typeof(AlertDashboardDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AlertDashboardDto>> GetDashboard(CancellationToken ct = default)
    {
        _logger.LogInformation("GET /api/alert/dashboard");
        var summary = await _alertService.GetDashboardAsync(ct);
        return Ok(summary);
    }

    /// <summary>
    /// Marks an alert as resolved. Sets ResolvedAt = UtcNow and records the resolving user.
    /// Idempotent: resolving an already-resolved alert returns 200 with the existing data.
    /// </summary>
    /// <param name="id">Alert primary key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated alert DTO.</returns>
    /// <response code="200">Alert resolved (or was already resolved).</response>
    /// <response code="404">Alert not found.</response>
    [HttpPost("{id:long}/resolve")]
    [ProducesResponseType(typeof(AlertDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AlertDto>> ResolveAlert(long id, CancellationToken ct = default)
    {
        _logger.LogInformation("POST /api/alert/{AlertId}/resolve", id);

        try
        {
            var userId = _currentUserService.GetCurrentUserId();
            var alert = await _alertService.ResolveAsync(id, userId, ct);
            return Ok(alert);
        }
        catch (KeyNotFoundException)
        {
            return NotFound($"Alert {id} not found.");
        }
    }
}
