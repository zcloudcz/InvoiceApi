using System.Security.Claims;
using Fakvio.Application.Exceptions;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.OAuth;
using Fakvio.Infrastructure.Authentication;
using Fakvio.Infrastructure.Authentication.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Fakvio.API.Controller;

/// <summary>
/// The consent screen's backing endpoints (ADR 0001, docs/adr/0001-mcp-oauth21.md §4.2/§4.9,
/// task N5.4): describe a ticket, and record the user's decision.
///
/// <c>[Authorize]</c> alone is not enough here: an API key or an OAuth access token also
/// satisfies it, and neither may drive a consent decision (T8 reasoning below extends to
/// "who" as well as "how"). <see cref="IsNotAJwtPrincipal"/> is the same rule
/// <c>ApiKeyRequestGuard</c> already applies to <c>/api/api-key</c> and
/// <c>/api/oauth/grants</c> — no scope claim means a JWT session.
///
/// CSRF (T8): the decision endpoint reads its credential from the <c>Authorization</c> header
/// (JWT), never a cookie — a forged cross-site form post cannot attach a header, so there is
/// nothing here for CSRF to forge. The ticket itself is a Data-Protection-sealed, 10-minute,
/// single-purpose token, not a session credential.
/// </summary>
[ApiController]
[Route("api/oauth/consent")]
[Authorize]
[EnableRateLimiting("oauth-consent")]
public class OAuthConsentController : ControllerBase
{
    private readonly IOAuthAuthorizeTicketProtector _ticketProtector;
    private readonly IOAuthService _oauthService;
    private readonly McpOAuthOptions _options;
    private readonly ILogger<OAuthConsentController> _logger;

    public OAuthConsentController(
        IOAuthAuthorizeTicketProtector ticketProtector,
        IOAuthService oauthService,
        IOptions<McpOAuthOptions> options,
        ILogger<OAuthConsentController> logger)
    {
        _ticketProtector = ticketProtector;
        _oauthService = oauthService;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Not a JWT session (API key or OAuth token) — same reasoning as ApiKeyRequestGuard.</summary>
    private bool IsNotAJwtPrincipal() => User.FindFirst(ApiKeyAuthenticationDefaults.ScopeClaimType) is not null;

    private long? GetUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(raw, out var id) ? id : null;
    }

    [HttpGet("{ticket}")]
    [ProducesResponseType(typeof(OAuthConsentInfoDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Describe(string ticket, CancellationToken ct)
    {
        if (!_options.Enabled)
            return NotFound();

        if (IsNotAJwtPrincipal())
            return Forbid();

        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();

        var payload = _ticketProtector.Unprotect(ticket);
        if (payload is null)
            return BadRequest(new { error = "invalid_ticket" });

        var userInfo = await _oauthService.GetConsentUserInfoAsync(userId.Value, ct, GetCompanyId());
        var isTrustedClient = IsTrustedHost(payload.ClientId);

        return Ok(new OAuthConsentInfoDto
        {
            ClientId = payload.ClientId,
            ClientName = payload.ClientName,
            RedirectUri = payload.RedirectUri,
            IsTrustedClient = isTrustedClient,
            IsLoopbackRedirect = IsLoopbackRedirectUri(payload.RedirectUri),
            RequestedScopes = payload.Scope,
            UserEmail = userInfo.Email,
            CompanyName = userInfo.CompanyName,
            CompanyId = GetCompanyId(),
            IsEligible = userInfo.IsEligible
        });
    }

    [HttpPost("decision")]
    [ProducesResponseType(typeof(OAuthConsentDecisionResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Decision([FromBody] OAuthConsentDecisionDto dto, CancellationToken ct)
    {
        if (!_options.Enabled)
            return NotFound();

        if (IsNotAJwtPrincipal())
            return Forbid();

        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();

        var payload = _ticketProtector.Unprotect(dto.Ticket);
        if (payload is null)
            return BadRequest(new { error = "invalid_ticket" });

        if (!dto.Allow)
        {
            _logger.LogWarning("OAuth.ConsentDenied: user {UserId} client {ClientId}", userId.Value, payload.ClientId);
            return Ok(new OAuthConsentDecisionResultDto { RedirectUrl = BuildRedirectUrl(payload, "error=access_denied") });
        }

        // A different tab may have switched the interactive session since the consent
        // screen was rendered. Require the exact company the user actually saw.
        if (dto.CompanyId is null || dto.CompanyId != GetCompanyId())
            return BadRequest(new { error = "company_changed", message = "Reload consent for the selected company." });

        // The granted scope may only narrow what was requested at /oauth/authorize — never
        // widen it (same T12 reasoning as the refresh endpoint). Silently clamping instead of
        // rejecting keeps a manipulated request from ever reaching a wider grant than the
        // authorize step itself agreed to.
        var grantedScope = ClampScope(dto.Scope, payload.Scope);

        try
        {
            var code = await _oauthService.IssueAuthorizationCodeAsync(
                new IssueAuthorizationCodeRequest(userId.Value, payload.ClientId, payload.ClientName, payload.RedirectUri, payload.CodeChallenge, grantedScope, payload.Resource, GetCompanyId()),
                ct);

            _logger.LogWarning("OAuth.ConsentGranted: user {UserId} client {ClientId} scope {Scope}", userId.Value, payload.ClientId, grantedScope);

            return Ok(new OAuthConsentDecisionResultDto
            {
                RedirectUrl = BuildRedirectUrl(payload, $"code={Uri.EscapeDataString(code)}")
            });
        }
        catch (OAuthErrorException ex) when (ex.ErrorCode == OAuthErrorException.AccessDenied)
        {
            // Closed early access (ADR §5.2): the user is not (or no longer) on the allowlist.
            return Ok(new OAuthConsentDecisionResultDto { RedirectUrl = BuildRedirectUrl(payload, "error=access_denied") });
        }
    }

    private long? GetCompanyId()
        => long.TryParse(User.FindFirstValue("CompanyId"), out var id) ? id : null;

    private string BuildRedirectUrl(OAuthAuthorizeTicketPayload payload, string outcomeQuery)
    {
        var separator = payload.RedirectUri.Contains('?') ? '&' : '?';
        var url = $"{payload.RedirectUri}{separator}{outcomeQuery}&iss={Uri.EscapeDataString(_options.Issuer ?? string.Empty)}";
        if (!string.IsNullOrEmpty(payload.State))
            url += $"&state={Uri.EscapeDataString(payload.State)}";
        return url;
    }

    private static string ClampScope(string? requested, string maxAllowed)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return maxAllowed;

        var requestsWrite = requested.Contains("write", StringComparison.OrdinalIgnoreCase);
        var allowsWrite = maxAllowed.Contains("write", StringComparison.OrdinalIgnoreCase);

        return requestsWrite && allowsWrite ? "read,write" : "read";
    }

    private bool IsTrustedHost(string clientId)
        => Uri.TryCreate(clientId, UriKind.Absolute, out var uri)
           && _options.TrustedClientHosts.Any(h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase));

    private static bool IsLoopbackRedirectUri(string redirectUri)
        => Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)
           && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
}
