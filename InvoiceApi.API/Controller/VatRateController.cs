using InvoiceApi.Contracts.Dto.VatRate;
using InvoiceApi.Application.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceApi.API.Controller;

/// <summary>
/// Controller for managing VAT rates (DPH sazby)
/// Provides CRUD operations for VAT rate configuration
/// All users can read VAT rates, only Admin/SysAdmin can modify
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize] // All endpoints require authentication
public class VatRateController : ControllerBase
{
    private readonly IVatRateService _vatRateService;
    private readonly ILogger<VatRateController> _logger;

    public VatRateController(
        IVatRateService vatRateService,
        ILogger<VatRateController> logger)
    {
        _vatRateService = vatRateService;
        _logger = logger;
    }

    /// <summary>
    /// Gets all VAT rates
    /// </summary>
    /// <param name="includeInactive">Include inactive rates in results</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of all VAT rates</returns>
    /// <response code="200">Returns list of VAT rates</response>
    [HttpGet]
    [ProducesResponseType(typeof(List<VatRateDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<VatRateDto>>> GetAllVatRates(
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/vatrate - includeInactive: {IncludeInactive}", includeInactive);
        var rates = await _vatRateService.GetAllVatRatesAsync(includeInactive, cancellationToken);
        return Ok(rates);
    }

    /// <summary>
    /// Gets a specific VAT rate by ID
    /// </summary>
    /// <param name="id">VAT rate ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>VAT rate data</returns>
    /// <response code="200">Returns the VAT rate</response>
    /// <response code="404">VAT rate not found</response>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(VatRateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VatRateDto>> GetVatRateById(
        long id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/vatrate/{Id}", id);

        var rate = await _vatRateService.GetVatRateByIdAsync(id, cancellationToken);

        if (rate == null)
        {
            _logger.LogWarning("VAT rate {Id} not found", id);
            return NotFound(new { message = $"VAT rate with ID {id} not found" });
        }

        return Ok(rate);
    }

    /// <summary>
    /// Gets active VAT rates valid at a specific date
    /// </summary>
    /// <param name="date">Date to check validity (defaults to current date if not provided)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of valid VAT rates for the given date</returns>
    /// <response code="200">Returns list of valid VAT rates</response>
    [HttpGet("active")]
    [ProducesResponseType(typeof(List<VatRateDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<VatRateDto>>> GetActiveVatRatesForDate(
        [FromQuery] DateTime? date = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/vatrate/active - date: {Date}", date ?? DateTime.UtcNow);
        var rates = await _vatRateService.GetActiveVatRatesForDateAsync(date, cancellationToken);
        return Ok(rates);
    }

    /// <summary>
    /// Gets the default standard VAT rate
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Default standard VAT rate</returns>
    /// <response code="200">Returns the default standard rate</response>
    /// <response code="404">No default standard rate configured</response>
    [HttpGet("default/standard")]
    [ProducesResponseType(typeof(VatRateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VatRateDto>> GetDefaultStandardRate(
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/vatrate/default/standard");

        var rate = await _vatRateService.GetDefaultStandardRateAsync(cancellationToken);

        if (rate == null)
        {
            _logger.LogWarning("No default standard VAT rate configured");
            return NotFound(new { message = "No default standard VAT rate configured" });
        }

        return Ok(rate);
    }

    /// <summary>
    /// Gets the default reduced VAT rate
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Default reduced VAT rate</returns>
    /// <response code="200">Returns the default reduced rate</response>
    /// <response code="404">No default reduced rate configured</response>
    [HttpGet("default/reduced")]
    [ProducesResponseType(typeof(VatRateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VatRateDto>> GetDefaultReducedRate(
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/vatrate/default/reduced");

        var rate = await _vatRateService.GetDefaultReducedRateAsync(cancellationToken);

        if (rate == null)
        {
            _logger.LogWarning("No default reduced VAT rate configured");
            return NotFound(new { message = "No default reduced VAT rate configured" });
        }

        return Ok(rate);
    }

    /// <summary>
    /// Creates a new VAT rate
    /// </summary>
    /// <param name="createDto">VAT rate creation data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created VAT rate</returns>
    /// <response code="201">VAT rate created successfully</response>
    /// <response code="400">Invalid data or validation error</response>
    [HttpPost]
    [Authorize(Roles = "Admin,SysAdmin")] // Only Admin and SysAdmin can create VAT rates
    [ProducesResponseType(typeof(VatRateDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<VatRateDto>> CreateVatRate(
        [FromBody] CreateVatRateDto createDto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("POST /api/vatrate - Creating VAT rate: {Name}", createDto.Name);

        try
        {
            var rate = await _vatRateService.CreateVatRateAsync(createDto, cancellationToken);
            return CreatedAtAction(nameof(GetVatRateById), new { id = rate.Id }, rate);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Failed to create VAT rate: {Message}", ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Updates an existing VAT rate
    /// </summary>
    /// <param name="id">VAT rate ID to update</param>
    /// <param name="updateDto">Updated VAT rate data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated VAT rate</returns>
    /// <response code="200">VAT rate updated successfully</response>
    /// <response code="400">Invalid data or validation error</response>
    /// <response code="404">VAT rate not found</response>
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin,SysAdmin")] // Only Admin and SysAdmin can update VAT rates
    [ProducesResponseType(typeof(VatRateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VatRateDto>> UpdateVatRate(
        long id,
        [FromBody] UpdateVatRateDto updateDto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("PUT /api/vatrate/{Id}", id);

        try
        {
            var rate = await _vatRateService.UpdateVatRateAsync(id, updateDto, cancellationToken);

            if (rate == null)
            {
                _logger.LogWarning("VAT rate {Id} not found", id);
                return NotFound(new { message = $"VAT rate with ID {id} not found" });
            }

            return Ok(rate);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Failed to update VAT rate {Id}: {Message}", id, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Deletes a VAT rate (soft delete - sets IsActive = false)
    /// </summary>
    /// <param name="id">VAT rate ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>No content</returns>
    /// <response code="204">VAT rate deleted successfully</response>
    /// <response code="400">Cannot delete - rate is used in invoices</response>
    /// <response code="404">VAT rate not found</response>
    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin,SysAdmin")] // Only Admin and SysAdmin can delete VAT rates
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteVatRate(
        long id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("DELETE /api/vatrate/{Id}", id);

        try
        {
            var deleted = await _vatRateService.DeleteVatRateAsync(id, cancellationToken);

            if (!deleted)
            {
                _logger.LogWarning("VAT rate {Id} not found", id);
                return NotFound(new { message = $"VAT rate with ID {id} not found" });
            }

            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Failed to delete VAT rate {Id}: {Message}", id, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Sets a VAT rate as default
    /// Automatically unsets the previous default rate of the same type (standard/reduced)
    /// </summary>
    /// <param name="id">VAT rate ID to set as default</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated VAT rate</returns>
    /// <response code="200">VAT rate set as default successfully</response>
    /// <response code="404">VAT rate not found</response>
    [HttpPost("{id}/set-default")]
    [Authorize(Roles = "Admin,SysAdmin")] // Only Admin and SysAdmin can set default VAT rates
    [ProducesResponseType(typeof(VatRateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VatRateDto>> SetAsDefault(
        long id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("POST /api/vatrate/{Id}/set-default", id);

        var rate = await _vatRateService.SetAsDefaultAsync(id, cancellationToken);

        if (rate == null)
        {
            _logger.LogWarning("VAT rate {Id} not found", id);
            return NotFound(new { message = $"VAT rate with ID {id} not found" });
        }

        return Ok(rate);
    }
}
