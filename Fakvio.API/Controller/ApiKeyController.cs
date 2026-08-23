using System.Security.Claims;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.ApiKey;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// API keys of the CURRENT user (identified by the NameIdentifier JWT claim).
/// A key is a personal credential, so there are deliberately no endpoints for
/// managing someone else's keys.
///
/// These endpoints are JWT-only: you cannot bootstrap or manage API keys with an
/// API key. Enforcing that is the job of the API-key authentication scheme (#236);
/// until it exists, JWT is the only credential the pipeline understands anyway.
/// </summary>
[ApiController]
[Route("api/api-key")]
[Produces("application/json")]
[Authorize]
public class ApiKeyController : ControllerBase
{
    private readonly IApiKeyService _apiKeyService;
    private readonly ILogger<ApiKeyController> _logger;

    public ApiKeyController(IApiKeyService apiKeyService, ILogger<ApiKeyController> logger)
    {
        _apiKeyService = apiKeyService;
        _logger = logger;
    }

    /// <summary>
    /// Reads the current user's id from the JWT (AuthService stores it in the
    /// standard NameIdentifier claim). Null when missing / malformed.
    /// </summary>
    private long? GetUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(raw, out var id) ? id : null;
    }

    /// <summary>
    /// Lists the current user's API keys (newest first). Shows the prefix, never the key.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ApiKeyDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ApiKeyDto>>> GetAll(CancellationToken ct = default)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        return Ok(await _apiKeyService.GetAllAsync(userId.Value, ct));
    }

    /// <summary>
    /// Creates a new API key. The response is the ONLY place the raw key appears —
    /// it is not recoverable afterwards.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(CreatedApiKeyDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CreatedApiKeyDto>> Create(
        [FromBody] CreateApiKeyDto dto,
        CancellationToken ct = default)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var created = await _apiKeyService.CreateAsync(userId.Value, dto, ct);
            return StatusCode(StatusCodes.Status201Created, created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Revokes one of the current user's API keys (soft — the row stays for audit).
    /// Returns 404 for an unknown key, someone else's key, or an already revoked one.
    /// </summary>
    [HttpPost("{id:long}/revoke")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(long id, CancellationToken ct = default)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        if (!await _apiKeyService.RevokeAsync(userId.Value, id, ct))
            return NotFound(new { message = $"API key {id} not found or already revoked." });

        _logger.LogInformation("User {UserId} revoked API key {ApiKeyId}", userId.Value, id);
        return NoContent();
    }
}
