using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.RecognizedCounterparty;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// CRUD for the recognized-counterparty registry — known bank accounts of
/// insurance companies, tax office etc. used to recognize recurring payments
/// that have no invoice. Tenant-scoped, requires authentication.
///
/// Create/Update responses include RecognizedCount — how many previously
/// unmatched transactions the automatic re-scan just recognized.
/// </summary>
[ApiController]
[Authorize]
[Route("api/recognized-counterparties")]
[Produces("application/json")]
public class RecognizedCounterpartyController : ControllerBase
{
    private readonly IRecognizedCounterpartyService _service;
    private readonly IPaymentMatchingService _matcher;
    private readonly ILogger<RecognizedCounterpartyController> _logger;

    public RecognizedCounterpartyController(
        IRecognizedCounterpartyService service,
        IPaymentMatchingService matcher,
        ILogger<RecognizedCounterpartyController> logger)
    {
        _service = service;
        _matcher = matcher;
        _logger = logger;
    }

    /// <summary>Lists all registry entries (including inactive), ordered by label.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<RecognizedCounterpartyDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<RecognizedCounterpartyDto>>> GetAll(CancellationToken ct = default)
    {
        return Ok(await _service.GetAllAsync(ct));
    }

    /// <summary>Gets a single registry entry.</summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType(typeof(RecognizedCounterpartyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecognizedCounterpartyDto>> GetById(long id, CancellationToken ct = default)
    {
        var dto = await _service.GetByIdAsync(id, ct);
        return dto == null ? NotFound() : Ok(dto);
    }

    /// <summary>Creates a registry entry and re-scans unmatched transactions.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(SaveRecognizedCounterpartyResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SaveRecognizedCounterpartyResponse>> Create(
        [FromBody] SaveRecognizedCounterpartyRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _service.CreateAsync(request, ct);
            return CreatedAtAction(nameof(GetById), new { id = result.Entry.Id }, result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Updates a registry entry and re-scans unmatched transactions.</summary>
    [HttpPut("{id:long}")]
    [ProducesResponseType(typeof(SaveRecognizedCounterpartyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SaveRecognizedCounterpartyResponse>> Update(
        long id,
        [FromBody] SaveRecognizedCounterpartyRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _service.UpdateAsync(id, request, ct);
            return result == null ? NotFound() : Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Deletes a registry entry. Transactions recognized by it are reset to
    /// Unmatched — categorization is derived data, safe to recompute.
    /// </summary>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct = default)
    {
        var deleted = await _service.DeleteAsync(id, ct);
        return deleted ? NoContent() : NotFound();
    }

    /// <summary>
    /// Manually re-runs recognition over all unmatched transactions.
    /// Returns the number of transactions newly recognized.
    /// </summary>
    [HttpPost("rescan")]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    public async Task<ActionResult<int>> Rescan(CancellationToken ct = default)
    {
        _logger.LogInformation("POST /api/recognized-counterparties/rescan");
        return Ok(await _matcher.RescanUnmatchedAsync(ct));
    }
}
