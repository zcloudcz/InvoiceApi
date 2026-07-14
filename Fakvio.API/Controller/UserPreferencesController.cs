using System.Security.Claims;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.User;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Per-user UI preferences of the CURRENT user (identified by the UserId JWT claim).
/// There are deliberately no admin endpoints for other users' preferences —
/// preferences are personal UI state, not something admins manage.
/// </summary>
[ApiController]
[Route("api/user-preferences")]
[Produces("application/json")]
[Authorize]
public class UserPreferencesController : ControllerBase
{
    private readonly IUserPreferencesService _preferencesService;
    private readonly ILogger<UserPreferencesController> _logger;

    public UserPreferencesController(
        IUserPreferencesService preferencesService,
        ILogger<UserPreferencesController> logger)
    {
        _preferencesService = preferencesService;
        _logger = logger;
    }

    /// <summary>
    /// Reads the current user's id from the JWT. AuthService stores it in the
    /// standard NameIdentifier claim (see GenerateJwtToken). Returns null when
    /// missing / malformed.
    /// </summary>
    private long? GetUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(raw, out var id) ? id : null;
    }

    /// <summary>
    /// Returns preferences of the current user (defaults when never saved).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(UserPreferencesDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<UserPreferencesDto>> Get(CancellationToken ct = default)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var result = await _preferencesService.GetAsync(userId.Value, ct);
        return Ok(result);
    }

    /// <summary>
    /// Creates or updates preferences of the current user.
    /// </summary>
    [HttpPut]
    [ProducesResponseType(typeof(UserPreferencesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UserPreferencesDto>> Update(
        [FromBody] UserPreferencesDto dto,
        CancellationToken ct = default)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var result = await _preferencesService.UpdateAsync(userId.Value, dto, ct);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
