using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.ExchangeRate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Official ČNB exchange rates (Master DB only — DEVGUIDE §4.17). Reading is open to every
/// authenticated user (invoice detail, MCP tool); the backfill is SysAdmin only.
/// The path is in TenantContextMiddleware.MasterOnlyPaths — no tenant context needed.
/// </summary>
[ApiController]
[Route("api/exchange-rate")]
[Produces("application/json")]
[Authorize]
public class ExchangeRateController : ControllerBase
{
    private readonly IExchangeRateService _rates;
    private readonly IExchangeRateSyncService _sync;

    public ExchangeRateController(IExchangeRateService rates, IExchangeRateSyncService sync)
    {
        _rates = rates;
        _sync = sync;
    }

    /// <summary>The rate valid on <paramref name="date"/> (default today): last ČNB fixing on or before it.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ExchangeRateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get([FromQuery] string currency, [FromQuery] DateOnly? date, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Trim().Length != 3 || currency.Trim().Equals("CZK", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "'currency' must be a 3-letter ISO code other than CZK." });

        var rate = await _rates.GetRateAsync(currency, date ?? DateOnly.FromDateTime(DateTime.UtcNow), ct);
        return rate is null
            ? NotFound(new { message = $"No ČNB rate available for '{currency.Trim().ToUpperInvariant()}'." })
            : Ok(rate);
    }

    /// <summary>Downloads the ČNB fixings for every business day in the range (SysAdmin, max 400 days).</summary>
    [HttpPost("backfill")]
    [Authorize(Roles = "SysAdmin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Backfill([FromBody] ExchangeRateBackfillDto dto, CancellationToken ct)
    {
        try
        {
            var days = await _sync.BackfillAsync(dto.From, dto.To, ct);
            return Ok(new { downloadedDays = days });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // ČNB unreachable / unparsable file — upstream problem, not a client error.
            return StatusCode(StatusCodes.Status502BadGateway, new { message = ex.Message });
        }
    }
}
