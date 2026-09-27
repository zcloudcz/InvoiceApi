using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.RecurringInvoice;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for managing recurring invoice schedules (DEVGUIDE §4.13).
/// GET endpoints work with a read-only API key; POST/PUT/DELETE require write scope
/// (enforced by ApiKeyRequestGuard — nothing to configure here, see DEVGUIDE §2.9-2.10).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize]
public class RecurringInvoiceController : ControllerBase
{
    private readonly IRecurringInvoiceService _recurringInvoiceService;
    private readonly ILogger<RecurringInvoiceController> _logger;

    public RecurringInvoiceController(IRecurringInvoiceService recurringInvoiceService, ILogger<RecurringInvoiceController> logger)
    {
        _recurringInvoiceService = recurringInvoiceService;
        _logger = logger;
    }

    /// <summary>
    /// Lists all recurring schedules, optionally filtered to a single template
    /// (used by the "Recurring schedule" panel on the invoice template detail page).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<RecurringInvoiceScheduleDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<RecurringInvoiceScheduleDto>>> GetAll([FromQuery] long? templateId, CancellationToken ct)
    {
        var result = templateId.HasValue
            ? await _recurringInvoiceService.GetByTemplateAsync(templateId.Value, ct)
            : await _recurringInvoiceService.GetAllAsync(ct);

        return Ok(result);
    }

    /// <summary>Gets a single schedule by ID.</summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType(typeof(RecurringInvoiceScheduleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecurringInvoiceScheduleDto>> GetById(long id, CancellationToken ct)
    {
        var schedule = await _recurringInvoiceService.GetByIdAsync(id, ct);
        return schedule != null ? Ok(schedule) : NotFound();
    }

    /// <summary>Creates a new recurring schedule for a template.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(RecurringInvoiceScheduleDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RecurringInvoiceScheduleDto>> Create(
        [FromBody] CreateRecurringInvoiceScheduleDto createDto, CancellationToken ct)
    {
        try
        {
            var result = await _recurringInvoiceService.CreateAsync(createDto, ct);
            return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Updates fields of an existing recurring schedule. Null fields are left unchanged.</summary>
    [HttpPut("{id:long}")]
    [ProducesResponseType(typeof(RecurringInvoiceScheduleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RecurringInvoiceScheduleDto>> Update(
        long id, [FromBody] UpdateRecurringInvoiceScheduleDto updateDto, CancellationToken ct)
    {
        try
        {
            var result = await _recurringInvoiceService.UpdateAsync(id, updateDto, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Pauses a schedule (IsActive = false) without deleting it.</summary>
    [HttpPost("{id:long}/pause")]
    [ProducesResponseType(typeof(RecurringInvoiceScheduleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RecurringInvoiceScheduleDto>> Pause(long id, CancellationToken ct)
    {
        try
        {
            return Ok(await _recurringInvoiceService.SetActiveAsync(id, isActive: false, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Resumes a paused schedule (IsActive = true).</summary>
    [HttpPost("{id:long}/resume")]
    [ProducesResponseType(typeof(RecurringInvoiceScheduleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RecurringInvoiceScheduleDto>> Resume(long id, CancellationToken ct)
    {
        try
        {
            return Ok(await _recurringInvoiceService.SetActiveAsync(id, isActive: true, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Deletes a schedule. Hard-deletes if it never fired, otherwise deactivates it
    /// (see IRecurringInvoiceService.DeleteAsync).
    /// </summary>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        try
        {
            await _recurringInvoiceService.DeleteAsync(id, ct);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
