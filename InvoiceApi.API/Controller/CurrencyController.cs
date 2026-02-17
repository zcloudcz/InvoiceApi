using InvoiceApi.Contracts.Dto.Currency;
using InvoiceApi.Application.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceApi.API.Controller;

/// <summary>
/// Controller for managing currencies.
/// Provides CRUD operations for currency master data.
/// Most operations require SysAdmin role, but reading active currencies is available to all authenticated users.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize]
public class CurrencyController : ControllerBase
{
    private readonly ICurrencyService _currencyService;
    private readonly ILogger<CurrencyController> _logger;

    public CurrencyController(ICurrencyService currencyService, ILogger<CurrencyController> logger)
    {
        _currencyService = currencyService;
        _logger = logger;
    }

    /// <summary>
    /// Gets all active currencies.
    /// Used by dropdowns in invoice forms, templates, etc.
    /// Available to all authenticated users.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of active currencies sorted by SortOrder</returns>
    /// <response code="200">Returns list of active currencies</response>
    [HttpGet("active")]
    [ProducesResponseType(typeof(List<CurrencyDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<CurrencyDto>>> GetActiveCurrencies(
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/currency/active");

        var currencies = await _currencyService.GetActiveCurrenciesAsync(cancellationToken);
        return Ok(currencies);
    }

    /// <summary>
    /// Gets paginated, filtered list of all currencies (including inactive).
    /// Only accessible by SysAdmin.
    /// </summary>
    /// <param name="page">Page number (1-based)</param>
    /// <param name="pageSize">Number of items per page</param>
    /// <param name="search">Search by code or name</param>
    /// <param name="isActive">Filter by active/inactive status</param>
    /// <param name="sortBy">Sort field (default: SortOrder)</param>
    /// <param name="isDescending">Sort direction</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Paged result of currencies</returns>
    /// <response code="200">Returns paged list of currencies</response>
    [HttpGet]
    [Authorize(Roles = "SysAdmin")]
    [ProducesResponseType(typeof(Contracts.Common.Pagination.PagedResult<CurrencyDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<Contracts.Common.Pagination.PagedResult<CurrencyDto>>> GetCurrenciesPaged(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? search = null,
        [FromQuery] bool? isActive = null,
        [FromQuery] string sortBy = "SortOrder",
        [FromQuery] bool isDescending = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/currency - Page: {Page}, PageSize: {PageSize}, Search: {Search}",
            page, pageSize, search);

        var result = await _currencyService.GetCurrenciesPagedAsync(
            page, pageSize, search, isActive, sortBy, isDescending, cancellationToken);

        return Ok(result);
    }

    /// <summary>
    /// Gets a specific currency by ID.
    /// </summary>
    /// <param name="id">Currency ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Currency data</returns>
    /// <response code="200">Returns the currency</response>
    /// <response code="404">Currency not found</response>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(CurrencyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CurrencyDto>> GetCurrencyById(
        long id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/currency/{Id}", id);

        var currency = await _currencyService.GetCurrencyByIdAsync(id, cancellationToken);

        if (currency == null)
        {
            return NotFound(new { message = $"Currency with ID {id} not found" });
        }

        return Ok(currency);
    }

    /// <summary>
    /// Creates a new currency. Only SysAdmin can create currencies.
    /// </summary>
    /// <param name="createDto">Currency data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created currency</returns>
    /// <response code="201">Currency created successfully</response>
    /// <response code="400">Invalid request data</response>
    /// <response code="409">Currency with this code already exists</response>
    [HttpPost]
    [Authorize(Roles = "SysAdmin")]
    [ProducesResponseType(typeof(CurrencyDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CurrencyDto>> CreateCurrency(
        [FromBody] CreateCurrencyDto createDto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("POST /api/currency - Creating currency {Code}", createDto.Code);

        try
        {
            var currency = await _currencyService.CreateCurrencyAsync(createDto, cancellationToken);
            return CreatedAtAction(nameof(GetCurrencyById), new { id = currency.Id }, currency);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists"))
        {
            return Conflict(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Updates an existing currency. Only SysAdmin can update currencies.
    /// Currency code cannot be changed.
    /// </summary>
    /// <param name="id">Currency ID</param>
    /// <param name="updateDto">Updated data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated currency</returns>
    /// <response code="200">Currency updated successfully</response>
    /// <response code="404">Currency not found</response>
    [HttpPut("{id}")]
    [Authorize(Roles = "SysAdmin")]
    [ProducesResponseType(typeof(CurrencyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CurrencyDto>> UpdateCurrency(
        long id,
        [FromBody] UpdateCurrencyDto updateDto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("PUT /api/currency/{Id}", id);

        var currency = await _currencyService.UpdateCurrencyAsync(id, updateDto, cancellationToken);

        if (currency == null)
        {
            return NotFound(new { message = $"Currency with ID {id} not found" });
        }

        return Ok(currency);
    }

    /// <summary>
    /// Deletes (soft delete) a currency. Only SysAdmin can delete currencies.
    /// Cannot delete a currency that is used in invoices.
    /// </summary>
    /// <param name="id">Currency ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>No content</returns>
    /// <response code="204">Currency deleted successfully</response>
    /// <response code="404">Currency not found</response>
    /// <response code="409">Currency is in use and cannot be deleted</response>
    [HttpDelete("{id}")]
    [Authorize(Roles = "SysAdmin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteCurrency(
        long id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("DELETE /api/currency/{Id}", id);

        try
        {
            var deleted = await _currencyService.DeleteCurrencyAsync(id, cancellationToken);

            if (!deleted)
            {
                return NotFound(new { message = $"Currency with ID {id} not found" });
            }

            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }
}
