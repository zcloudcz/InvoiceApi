using Fakvio.Application.Exceptions;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.OAuth;
using Fakvio.Infrastructure.Authentication.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Fakvio.API.Controller;

/// <summary>
/// The OAuth 2.1 authorization server's protocol endpoints: AS metadata, token, revoke.
/// Per ADR 0001 (docs/adr/0001-mcp-oauth21.md) §4.1/§4.2. Authorize + consent live in
/// <c>OAuthAuthorizeController</c> (N5.4); grant management lives in
/// <c>OAuthGrantsController</c> (N5.7).
///
/// Every action starts by checking <c>McpOAuth:Enabled</c> and answers 404 when it is off —
/// the flag's whole point (ADR §5.1) is that today's behaviour is unchanged until it is
/// deliberately turned on, so there is nothing here for a scanner or a misconfigured client
/// to even discover.
///
/// Deliberately anonymous: a public OAuth client (per ADR §3, "public client, no secret") has
/// no first-party credential to authenticate this controller's endpoints with — the security
/// boundary is PKCE + the single-use code/token, not a bearer credential on these requests.
/// </summary>
[ApiController]
[AllowAnonymous]
[Produces("application/json")]
public class OAuthController : ControllerBase
{
    private readonly IOAuthService _oauthService;
    private readonly McpOAuthOptions _options;
    private readonly ILogger<OAuthController> _logger;

    public OAuthController(IOAuthService oauthService, IOptions<McpOAuthOptions> options, ILogger<OAuthController> logger)
    {
        _oauthService = oauthService;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>RFC 8414 Authorization Server Metadata (ADR §4.1).</summary>
    [HttpGet("/.well-known/oauth-authorization-server")]
    [ProducesResponseType(typeof(OAuthAuthorizationServerMetadataDto), StatusCodes.Status200OK)]
    public IActionResult Metadata()
    {
        if (!_options.Enabled)
            return NotFound();

        var issuer = _options.Issuer ?? string.Empty;

        return Ok(new OAuthAuthorizationServerMetadataDto
        {
            Issuer = issuer,
            AuthorizationEndpoint = $"{issuer}/oauth/authorize",
            TokenEndpoint = $"{issuer}/oauth/token",
            RevocationEndpoint = $"{issuer}/oauth/revoke"
        });
    }

    /// <summary>
    /// The token endpoint (ADR §4.2). Accepts both grant types this AS supports;
    /// <c>application/x-www-form-urlencoded</c> per RFC 6749.
    /// </summary>
    [HttpPost("/oauth/token")]
    [EnableRateLimiting("oauth-token")]
    [Consumes("application/x-www-form-urlencoded")]
    [ProducesResponseType(typeof(OAuthTokenResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(OAuthErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Token(
        [FromForm(Name = "grant_type")] string? grantType,
        [FromForm(Name = "code")] string? code,
        [FromForm(Name = "redirect_uri")] string? redirectUri,
        [FromForm(Name = "client_id")] string? clientId,
        [FromForm(Name = "code_verifier")] string? codeVerifier,
        [FromForm(Name = "refresh_token")] string? refreshToken,
        [FromForm(Name = "scope")] string? scope,
        [FromForm(Name = "resource")] string? resource,
        CancellationToken ct)
    {
        // RFC 6749 §5.1: token responses must never be cached — the whole point is that the
        // body carries a fresh secret every time.
        Response.Headers.CacheControl = "no-store";

        if (!_options.Enabled)
            return NotFound();

        try
        {
            OAuthTokenResult result = grantType switch
            {
                "authorization_code" => await ExchangeAsync(code, redirectUri, clientId, codeVerifier, resource, ct),
                "refresh_token" => await RefreshAsync(refreshToken, scope, resource, ct),
                _ => throw new OAuthErrorException(OAuthErrorException.UnsupportedGrantType, $"unsupported grant_type '{grantType}'")
            };

            return Ok(new OAuthTokenResponseDto
            {
                AccessToken = result.AccessToken,
                RefreshToken = result.RefreshToken,
                ExpiresIn = result.ExpiresIn,
                Scope = FormatScopeForResponse(result.Scope)
            });
        }
        catch (OAuthErrorException ex)
        {
            // The diagnostic message (ex.Message) is server-only — the response carries just
            // the RFC 6749 error code, never the reason (ADR §4.2: "Chyby jen RFC 6749 kódy
            // bez interních detailů").
            _logger.LogWarning("OAuth token request rejected: {ErrorCode} — {Diagnostic}", ex.ErrorCode, ex.Message);
            return BadRequest(new OAuthErrorResponseDto { Error = ex.ErrorCode });
        }
    }

    private Task<OAuthTokenResult> ExchangeAsync(
        string? code, string? redirectUri, string? clientId, string? codeVerifier, string? resource, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(redirectUri) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(codeVerifier))
            throw new OAuthErrorException(OAuthErrorException.InvalidRequest, "code, redirect_uri, client_id and code_verifier are all required");

        return _oauthService.ExchangeAuthorizationCodeAsync(
            new ExchangeAuthorizationCodeRequest(code, redirectUri, clientId, codeVerifier, resource), ct);
    }

    /// <summary>
    /// Converts the application's comma-separated scope storage into the space-separated
    /// format required by OAuth token responses (RFC 6749 §3.3).
    /// </summary>
    private static string FormatScopeForResponse(string scope)
        => string.Join(' ', scope.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    private Task<OAuthTokenResult> RefreshAsync(string? refreshToken, string? scope, string? resource, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(refreshToken))
            throw new OAuthErrorException(OAuthErrorException.InvalidRequest, "refresh_token is required");

        return _oauthService.RefreshAsync(new RefreshTokenRequest(refreshToken, scope, resource), ct);
    }

    /// <summary>RFC 7009 token revocation (ADR §4.2 "Revoke"). Always 200 — see IOAuthService.RevokeAsync.</summary>
    [HttpPost("/oauth/revoke")]
    [EnableRateLimiting("oauth-token")]
    [Consumes("application/x-www-form-urlencoded")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Revoke(
        [FromForm(Name = "token")] string? token,
        [FromForm(Name = "token_type_hint")] string? tokenTypeHint,
        CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";

        if (!_options.Enabled)
            return NotFound();

        if (!string.IsNullOrEmpty(token))
            await _oauthService.RevokeAsync(token, tokenTypeHint, ct);

        return Ok();
    }
}
