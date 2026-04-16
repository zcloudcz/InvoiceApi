using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Reminder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for managing payment reminders (dunning).
/// Provides endpoints for:
///   - Settings CRUD (company default + per-client overrides)
///   - Reminder listing with pagination and filtering
///   - Manual send/cancel actions
///   - Dashboard summary data
///
/// All endpoints require authentication. Tenant isolation is automatic via JWT CompanyId.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize]
public class ReminderController : ControllerBase
{
    private readonly IReminderService _reminderService;
    private readonly ILogger<ReminderController> _logger;

    public ReminderController(IReminderService reminderService, ILogger<ReminderController> logger)
    {
        _reminderService = reminderService;
        _logger = logger;
    }

    // ─── Settings endpoints ─────────────────────────────────────────────

    /// <summary>
    /// Gets company-level default reminder settings. Creates with defaults if not found.
    /// </summary>
    [HttpGet("settings")]
    [ProducesResponseType(typeof(ReminderSettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ReminderSettingsDto>> GetCompanySettings(CancellationToken ct)
    {
        var settings = await _reminderService.GetCompanySettingsAsync(ct);
        return Ok(settings);
    }

    /// <summary>
    /// Gets effective settings for a specific client.
    /// Returns client-level override if it exists, otherwise company default.
    /// </summary>
    [HttpGet("settings/client/{clientId:long}")]
    [ProducesResponseType(typeof(ReminderSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReminderSettingsDto>> GetClientSettings(long clientId, CancellationToken ct)
    {
        var settings = await _reminderService.GetEffectiveSettingsAsync(clientId, ct);
        if (settings == null)
            return NotFound();

        return Ok(settings);
    }

    /// <summary>
    /// Creates or updates reminder settings (upsert).
    /// If ClientId is null, updates company default. If set, creates/updates client override.
    /// </summary>
    [HttpPut("settings")]
    [ProducesResponseType(typeof(ReminderSettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ReminderSettingsDto>> UpsertSettings(
        [FromBody] UpdateReminderSettingsDto dto, CancellationToken ct)
    {
        var result = await _reminderService.UpsertSettingsAsync(dto, ct);
        return Ok(result);
    }

    /// <summary>
    /// Deletes a client-level override, reverting to company default for that client.
    /// </summary>
    [HttpDelete("settings/client/{clientId:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteClientSettings(long clientId, CancellationToken ct)
    {
        var deleted = await _reminderService.DeleteClientSettingsAsync(clientId, ct);
        return deleted ? NoContent() : NotFound();
    }

    /// <summary>
    /// Lists all client-level overrides (for the settings management page).
    /// </summary>
    [HttpGet("settings/overrides")]
    [ProducesResponseType(typeof(List<ReminderSettingsDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ReminderSettingsDto>>> GetClientOverrides(CancellationToken ct)
    {
        var overrides = await _reminderService.GetClientOverridesAsync(ct);
        return Ok(overrides);
    }

    // ─── Reminder listing endpoints ─────────────────────────────────────

    /// <summary>
    /// Lists reminders with pagination and filtering.
    /// Supports filtering by status, client, invoice, level, and date range.
    /// </summary>
    [HttpGet("paged")]
    [ProducesResponseType(typeof(PagedResult<ReminderDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult> GetPaged([FromQuery] ReminderFilterDto filter, CancellationToken ct)
    {
        var result = await _reminderService.GetRemindersPagedAsync(filter, ct);
        return Ok(result);
    }

    /// <summary>
    /// Lists all reminders for a specific invoice, ordered by level.
    /// </summary>
    [HttpGet("invoice/{invoiceId:long}")]
    [ProducesResponseType(typeof(List<ReminderDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ReminderDto>>> GetByInvoice(long invoiceId, CancellationToken ct)
    {
        var reminders = await _reminderService.GetByInvoiceAsync(invoiceId, ct);
        return Ok(reminders);
    }

    /// <summary>
    /// Gets a single reminder by ID.
    /// </summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType(typeof(ReminderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReminderDto>> GetById(long id, CancellationToken ct)
    {
        var reminder = await _reminderService.GetByIdAsync(id, ct);
        return reminder != null ? Ok(reminder) : NotFound();
    }

    // ─── Action endpoints ───────────────────────────────────────────────

    /// <summary>
    /// Manually sends a Draft reminder (resolves template, sends email, updates status).
    /// </summary>
    [HttpPost("{id:long}/send")]
    [ProducesResponseType(typeof(ReminderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ReminderDto>> SendReminder(long id, CancellationToken ct)
    {
        try
        {
            var result = await _reminderService.SendReminderAsync(id, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Cancels a reminder with optional notes.
    /// </summary>
    [HttpPost("{id:long}/cancel")]
    [ProducesResponseType(typeof(ReminderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ReminderDto>> CancelReminder(long id, [FromQuery] string? notes, CancellationToken ct)
    {
        try
        {
            var result = await _reminderService.CancelReminderAsync(id, notes, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ─── Dashboard endpoint ─────────────────────────────────────────────

    /// <summary>
    /// Gets summary statistics for the dashboard widget.
    /// </summary>
    [HttpGet("dashboard")]
    [ProducesResponseType(typeof(ReminderDashboardDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ReminderDashboardDto>> GetDashboardData(CancellationToken ct)
    {
        var data = await _reminderService.GetDashboardDataAsync(ct);
        return Ok(data);
    }
}
