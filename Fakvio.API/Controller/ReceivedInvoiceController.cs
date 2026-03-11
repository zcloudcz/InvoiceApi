using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Application.Service;
using Fakvio.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for managing received (incoming) invoices — expenses from suppliers.
/// Provides CRUD operations and status lifecycle management (Received -> Approved -> Paid).
/// All endpoints require authentication; tenant isolation via CompanyId in JWT.
/// </summary>
[ApiController]
[Route("api/received-invoice")]
[Produces("application/json")]
[Authorize]
public class ReceivedInvoiceController : ControllerBase
{
    private readonly IReceivedInvoiceService _service;
    private readonly ILogger<ReceivedInvoiceController> _logger;

    public ReceivedInvoiceController(
        IReceivedInvoiceService service,
        ILogger<ReceivedInvoiceController> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <summary>
    /// Gets all received invoices with optional filtering.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<ReceivedInvoiceDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ReceivedInvoiceDto>>> GetAll(
        [FromQuery] EReceivedInvoiceStatus? status = null,
        [FromQuery] long? supplierId = null,
        CancellationToken ct = default)
    {
        var result = await _service.GetAllAsync(status, supplierId, ct);
        return Ok(result);
    }

    /// <summary>
    /// Gets paginated, filtered and sorted received invoices.
    /// Used by UI data grids with server-side pagination.
    /// </summary>
    [HttpGet("paged")]
    [ProducesResponseType(typeof(PagedResult<ReceivedInvoiceDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<ReceivedInvoiceDto>>> GetPaged(
        [FromQuery] ReceivedInvoiceFilterDto filter,
        CancellationToken ct = default)
    {
        var result = await _service.GetPagedAsync(filter, ct);
        return Ok(result);
    }

    /// <summary>
    /// Gets a specific received invoice by ID.
    /// </summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType(typeof(ReceivedInvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReceivedInvoiceDto>> GetById(long id, CancellationToken ct = default)
    {
        var result = await _service.GetByIdAsync(id, ct);
        if (result is null) return NotFound();
        return Ok(result);
    }

    /// <summary>
    /// Creates a new received invoice.
    /// Automatically calculates item totals and invoice totals.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ReceivedInvoiceDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ReceivedInvoiceDto>> Create(
        [FromBody] CreateReceivedInvoiceDto dto,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _service.CreateAsync(dto, ct);
            return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Updates an existing received invoice.
    /// Only allowed in Received or Approved status.
    /// </summary>
    [HttpPut("{id:long}")]
    [ProducesResponseType(typeof(ReceivedInvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReceivedInvoiceDto>> Update(
        long id,
        [FromBody] UpdateReceivedInvoiceDto dto,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _service.UpdateAsync(id, dto, ct);
            if (result is null) return NotFound();
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Approves a received invoice for payment.
    /// Transition: Received -> Approved.
    /// </summary>
    [HttpPost("{id:long}/approve")]
    [ProducesResponseType(typeof(ReceivedInvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReceivedInvoiceDto>> Approve(long id, CancellationToken ct = default)
    {
        try
        {
            var result = await _service.ApproveAsync(id, ct);
            if (result is null) return NotFound();
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Marks a received invoice as paid.
    /// Transition: Approved -> Paid.
    /// </summary>
    [HttpPost("{id:long}/mark-paid")]
    [ProducesResponseType(typeof(ReceivedInvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReceivedInvoiceDto>> MarkAsPaid(
        long id,
        [FromQuery] DateTime? paidAt = null,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _service.MarkAsPaidAsync(id, paidAt, ct);
            if (result is null) return NotFound();
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Rejects a received invoice.
    /// Transition: Received -> Rejected.
    /// </summary>
    [HttpPost("{id:long}/reject")]
    [ProducesResponseType(typeof(ReceivedInvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReceivedInvoiceDto>> Reject(long id, CancellationToken ct = default)
    {
        try
        {
            var result = await _service.RejectAsync(id, ct);
            if (result is null) return NotFound();
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Soft-deletes a received invoice.
    /// Only allowed in Received or Rejected status.
    /// </summary>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct = default)
    {
        try
        {
            var deleted = await _service.DeleteAsync(id, ct);
            if (!deleted) return NotFound();
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
