using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Vies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Verifies EU VAT identification numbers (DIČ) against VIES.
/// Separate from <see cref="ClientController"/>'s ARES endpoint — VIES is EU-wide VAT
/// registry data, ARES is the Czech company registry; they answer different questions
/// (is this VAT ID currently registered? vs. what is this Czech company's data?).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize]
public class ViesController : ControllerBase
{
    private readonly IViesService _viesService;
    private readonly ILogger<ViesController> _logger;

    public ViesController(IViesService viesService, ILogger<ViesController> logger)
    {
        _viesService = viesService;
        _logger = logger;
    }

    /// <summary>
    /// Verifies a VAT ID against VIES (e.g. "CZ12345678", "DE 123 456 789").
    /// Always returns 200 — the result's <c>status</c> distinguishes Valid / Invalid /
    /// Unavailable, so a VIES outage is never reported to the caller as "invalid DIČ".
    /// </summary>
    /// <param name="vatId">VAT ID including its 2-letter country prefix.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("{vatId}")]
    [ProducesResponseType(typeof(ViesVerificationResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<ViesVerificationResult>> VerifyVatId(
        string vatId,
        CancellationToken cancellationToken = default)
    {
        // Log only the shape of the request, never the full VAT ID — avoids PII-ish data in logs.
        _logger.LogInformation("GET /api/vies/{Length}-char VAT ID", vatId?.Length ?? 0);

        var result = await _viesService.VerifyAsync(vatId ?? string.Empty, cancellationToken);
        return Ok(result);
    }
}
