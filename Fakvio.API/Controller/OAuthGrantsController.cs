using System.Security.Claims;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.OAuth;
using Fakvio.Infrastructure.Authentication.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Fakvio.API.Controller;

/// <summary>
/// "Připojené aplikace" (ADR 0001, docs/adr/0001-mcp-oauth21.md §4.8) — the current user's own
/// live OAuth grants. Same shape as <c>ApiKeyController</c>: everything is scoped to the caller,
/// so there are no endpoints for managing someone else's grants.
///
/// JWT-only is enforced globally by <c>ApiKeyRequestGuard</c> (the "/api/oauth/grants" prefix
/// added alongside "/api/api-key" in N5.3) — neither a manually created API key nor an
/// OAuth-issued access token may reach this controller at all, so there is nothing to check here.
/// </summary>
[ApiController]
[Route("api/oauth/grants")]
[Produces("application/json")]
[Authorize]
public class OAuthGrantsController : ControllerBase
{
    private readonly IOAuthService _oauthService;
    private readonly McpOAuthOptions _options;
    private readonly ILogger<OAuthGrantsController> _logger;

    public OAuthGrantsController(IOAuthService oauthService, IOptions<McpOAuthOptions> options, ILogger<OAuthGrantsController> logger)
    {
        _oauthService = oauthService;
        _options = options.Value;
        _logger = logger;
    }

    private long? GetUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(raw, out var id) ? id : null;
    }

    /// <summary>Lists the current user's live grants, newest first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<OAuthGrantDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<OAuthGrantDto>>> GetAll(CancellationToken ct = default)
    {
        if (!_options.Enabled) return NotFound();

        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        return Ok(await _oauthService.GetGrantsAsync(userId.Value, ct));
    }

    /// <summary>
    /// Revokes one of the current user's grants — and, in the same operation, every access
    /// token and refresh token issued under it (ADR §4.3/§4.8: the next MCP request with that
    /// token gets 401 immediately, no cache to wait out).
    /// </summary>
    [HttpPost("{id:long}/revoke")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(long id, CancellationToken ct = default)
    {
        if (!_options.Enabled) return NotFound();

        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        if (!await _oauthService.RevokeGrantForUserAsync(userId.Value, id, ct))
            return NotFound(new { message = $"OAuth grant {id} not found or already revoked." });

        _logger.LogInformation("User {UserId} revoked OAuth grant {GrantId}", userId.Value, id);
        return NoContent();
    }
}
