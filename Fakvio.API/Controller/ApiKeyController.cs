using System.Security.Claims;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.ApiKey;
using Fakvio.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// API keys of the CURRENT user (identified by the NameIdentifier JWT claim).
/// A key is a personal credential, so there are deliberately no endpoints for
/// managing someone else's keys.
///
/// Management here is JWT-only: you cannot bootstrap or manage API keys with an API key.
/// <c>ApiKeyRequestGuard</c> enforces it for both hosts by refusing an API-key principal
/// on everything under /api/api-key except <see cref="ApiKeyController.Me"/>.
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
    /// Reads the current user's id from the standard NameIdentifier claim — both the JWT
    /// path (AuthService) and the API-key path (ApiKeyAuthenticator) put it there.
    /// Null when missing / malformed.
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
    /// Reports what the presented credential authenticates as. The one endpoint under
    /// /api/api-key an API key may call itself (see <c>ApiKeyRequestGuard</c>) — machine
    /// clients use it to validate a key and read its scopes before doing real work.
    /// Answers for a JWT session too, with the key-specific fields null.
    /// </summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(ApiKeyIdentityDto), StatusCodes.Status200OK)]
    public ActionResult<ApiKeyIdentityDto> Me()
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        // Read straight off the principal — whichever middleware built it (JWT validation
        // or ApiKeyAuthenticator) put the same claims there, and impersonation has already
        // rewritten CompanyId by the time we get here.
        var companyId = User.FindFirstValue("CompanyId");
        var apiKeyId = User.FindFirstValue(ApiKeyAuthenticationDefaults.KeyIdClaimType);
        var oauthGrantId = User.FindFirstValue(ApiKeyAuthenticationDefaults.OAuthGrantIdClaimType);

        return Ok(new ApiKeyIdentityDto
        {
            UserId = userId.Value,
            Email = User.FindFirstValue(ClaimTypes.Email) ?? string.Empty,
            FullName = User.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
            Role = User.FindFirstValue(ClaimTypes.Role) ?? string.Empty,
            CompanyId = long.TryParse(companyId, out var parsedCompanyId) ? parsedCompanyId : null,
            Scopes = User.FindFirstValue(ApiKeyAuthenticationDefaults.ScopeClaimType),
            ApiKeyId = long.TryParse(apiKeyId, out var parsedKeyId) ? parsedKeyId : null,
            OAuthGrantId = long.TryParse(oauthGrantId, out var parsedGrantId) ? parsedGrantId : null,
            OAuthResource = User.FindFirstValue(ApiKeyAuthenticationDefaults.OAuthResourceClaimType)
        });
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
            // Bind an omitted default to the currently selected interactive company.
            // The service validates any explicit selection against live memberships.
            if (dto.CompanyId is null && long.TryParse(User.FindFirstValue("CompanyId"), out var selectedCompanyId))
                dto.CompanyId = selectedCompanyId;
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
