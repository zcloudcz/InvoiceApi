using Fakvio.Contracts.Dto.ReverseChargeCode;
using Fakvio.Application.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Read-only controller for reverse charge codes (kódy předmětu plnění PDP).
///
/// Reverse charge codes are MFČR reference data that identify the type of supply
/// subject to the Czech reverse charge mechanism (§92a–§92e ZDPH).
/// They are used in:
///   - Invoice line items with VatRegime == ReverseCharge
///   - VAT control statement (kontrolní hlášení / EPO XML) — sections A.1 and B.1
///
/// This controller is READ-ONLY — admin CRUD is handled in ReverseChargeCodes.razor (#49).
/// Authorization: any authenticated user (the dropdown is needed by all invoice editors).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize] // Any authenticated user can read reverse charge codes for the dropdown
public class ReverseChargeCodeController : ControllerBase
{
    private readonly IReverseChargeCodeService _service;
    private readonly ILogger<ReverseChargeCodeController> _logger;

    public ReverseChargeCodeController(
        IReverseChargeCodeService service,
        ILogger<ReverseChargeCodeController> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <summary>
    /// Returns all currently active and valid reverse charge codes.
    ///
    /// A code is considered active when:
    ///   - IsActive == true
    ///   - ValidFrom &lt;= today
    ///   - ValidTo == null OR ValidTo &gt;= today
    ///
    /// Used to populate the PDP code dropdown in the invoice line-item editor.
    /// Results are ordered by Code ascending (e.g., "1", "1a", "3", "3a", ...).
    /// </summary>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    /// <returns>List of active, currently valid reverse charge codes.</returns>
    /// <response code="200">Returns the list of active codes (may be empty if none configured).</response>
    [HttpGet]
    [ProducesResponseType(typeof(List<ReverseChargeCodeDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ReverseChargeCodeDto>>> GetAllActive(
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/reversechargecode — fetching active codes");

        var codes = await _service.GetAllActiveAsync(cancellationToken);
        return Ok(codes);
    }

    /// <summary>
    /// Returns a single reverse charge code by its database ID.
    ///
    /// Used by admin edit pages (task #49) to load the current record before editing.
    /// Returns the record regardless of IsActive status — admin needs to see inactive records too.
    /// Returns 404 when no record with the given ID exists.
    /// </summary>
    /// <param name="id">Database primary key (BaseEntity.Id).</param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    /// <returns>The matching code DTO, or 404 if not found.</returns>
    /// <response code="200">Returns the reverse charge code.</response>
    /// <response code="404">No code with the given ID found.</response>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ReverseChargeCodeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReverseChargeCodeDto>> GetById(
        long id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/reversechargecode/{Id}", id);

        var code = await _service.GetByIdAsync(id, cancellationToken);

        if (code == null)
        {
            _logger.LogWarning("Reverse charge code {Id} not found", id);
            return NotFound(new { message = $"Reverse charge code with ID {id} not found" });
        }

        return Ok(code);
    }
}
