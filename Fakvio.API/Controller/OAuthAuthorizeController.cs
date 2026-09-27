using System.Text.Encodings.Web;
using Fakvio.Application.Exceptions;
using Fakvio.Application.Service;
using Fakvio.Infrastructure.Authentication.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Fakvio.API.Controller;

/// <summary>
/// <c>GET /oauth/authorize</c> — the authorization endpoint (ADR 0001,
/// docs/adr/0001-mcp-oauth21.md §4.2/§4.6). Anonymous: the caller has not logged in yet, that
/// happens on the consent page (<c>OAuthConsentController</c>, N5.4).
///
/// The one hard rule that shapes this whole controller (ADR §4.2 step 2): "Chyba v client_id
/// nebo redirect_uri → nikdy nepřesměrovat, chybová stránka na Fakvio doméně. Ostatní chyby →
/// redirect na ověřený redirect_uri s error, state, iss." An unvalidated redirect_uri is
/// exactly the shape of an open-redirect vulnerability (T2), so it is validated FIRST and
/// everything that can still go wrong afterwards is safe to report back to the client via
/// that now-trusted URL instead of a same-origin error page.
/// </summary>
[ApiController]
[AllowAnonymous]
public class OAuthAuthorizeController : ControllerBase
{
    private readonly IOAuthClientResolver _clientResolver;
    private readonly IOAuthService _oauthService;
    private readonly IOAuthAuthorizeTicketProtector _ticketProtector;
    private readonly McpOAuthOptions _options;
    private readonly ILogger<OAuthAuthorizeController> _logger;

    public OAuthAuthorizeController(
        IOAuthClientResolver clientResolver,
        IOAuthService oauthService,
        IOAuthAuthorizeTicketProtector ticketProtector,
        IOptions<McpOAuthOptions> options,
        ILogger<OAuthAuthorizeController> logger)
    {
        _clientResolver = clientResolver;
        _oauthService = oauthService;
        _ticketProtector = ticketProtector;
        _options = options.Value;
        _logger = logger;
    }

    [HttpGet("/oauth/authorize")]
    [EnableRateLimiting("oauth-authorize")]
    public async Task<IActionResult> Authorize(
        [FromQuery(Name = "response_type")] string? responseType,
        [FromQuery(Name = "client_id")] string? clientId,
        [FromQuery(Name = "redirect_uri")] string? redirectUri,
        [FromQuery(Name = "code_challenge")] string? codeChallenge,
        [FromQuery(Name = "code_challenge_method")] string? codeChallengeMethod,
        [FromQuery(Name = "resource")] string? resource,
        [FromQuery(Name = "scope")] string? scope,
        [FromQuery(Name = "state")] string? state,
        CancellationToken ct)
    {
        if (!_options.Enabled)
            return NotFound();

        // ── Step 1: client_id + redirect_uri — failure here NEVER redirects ────────────
        if (string.IsNullOrWhiteSpace(clientId))
            return ErrorPage("client_id is required.");

        var document = await _clientResolver.ResolveAsync(clientId, ct);
        if (document is null)
            return ErrorPage("client_id could not be resolved to a trusted client.");

        if (string.IsNullOrWhiteSpace(redirectUri) || !document.RedirectUris.Any(allowed => RedirectUriMatches(redirectUri, allowed)))
            return ErrorPage("redirect_uri does not match any redirect_uri registered for this client.");

        // ── Step 2: everything else — failure redirects to the now-trusted redirect_uri ──
        try
        {
            if (!string.Equals(responseType, "code", StringComparison.Ordinal))
                throw new OAuthErrorException(OAuthErrorException.InvalidRequest, $"unsupported response_type '{responseType}'");

            // PKCE S256 is mandatory; plain is not merely discouraged, there is no code path
            // for it at all (ADR §4.2/§4.6, threat T3).
            if (!string.Equals(codeChallengeMethod, "S256", StringComparison.Ordinal))
                throw new OAuthErrorException(OAuthErrorException.InvalidRequest, $"code_challenge_method must be S256, got '{codeChallengeMethod}'");

            // RFC 7636 §4.2: 43-128 characters of the unreserved URL-safe alphabet. Checking
            // length only here — the token endpoint is what actually verifies the challenge.
            if (string.IsNullOrEmpty(codeChallenge) || codeChallenge.Length is < 43 or > 128)
                throw new OAuthErrorException(OAuthErrorException.InvalidRequest, "code_challenge is missing or has an invalid length");

            var canonicalResource = _oauthService.ResolveCanonicalResource(resource);

            var normalizedScope = NormalizeRequestedScope(scope);

            var ticket = _ticketProtector.Protect(new OAuthAuthorizeTicketPayload(
                document.ClientId, document.ClientName, redirectUri, codeChallenge, normalizedScope, canonicalResource, state));

            var consentUrl = _options.ConsentUrl ?? throw new InvalidOperationException("McpOAuth:ConsentUrl is not configured.");
            return Redirect($"{consentUrl}?ticket={UrlEncoder.Default.Encode(ticket)}");
        }
        catch (OAuthErrorException ex)
        {
            _logger.LogWarning("OAuth authorize request rejected: {ErrorCode} — {Diagnostic}", ex.ErrorCode, ex.Message);
            return Redirect(BuildErrorRedirectUrl(redirectUri, ex.ErrorCode, state));
        }
    }

    /// <summary>
    /// client_id/redirect_uri errors land here — a same-origin page, never a redirect to
    /// attacker-controlled input (ADR §4.2 step 2, threat T2).
    /// </summary>
    private IActionResult ErrorPage(string reason)
    {
        _logger.LogWarning("OAuth.ClientRejected: authorize request refused before any redirect — {Reason}", reason);
        return BadRequest(new { error = "invalid_request", message = "This authorization request could not be processed." });
    }

    private string BuildErrorRedirectUrl(string redirectUri, string errorCode, string? state)
    {
        var separator = redirectUri.Contains('?') ? '&' : '?';
        var url = $"{redirectUri}{separator}error={Uri.EscapeDataString(errorCode)}&iss={Uri.EscapeDataString(_options.Issuer ?? string.Empty)}";
        if (!string.IsNullOrEmpty(state))
            url += $"&state={Uri.EscapeDataString(state)}";
        return url;
    }

    /// <summary>ADR §4.2: empty scope means "both requested, user chooses"; otherwise must be a subset of read/write.</summary>
    private static string NormalizeRequestedScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return "read,write";

        var parts = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "read", "write" };

        foreach (var part in parts)
        {
            if (!allowed.Contains(part))
                throw new OAuthErrorException(OAuthErrorException.InvalidScope, $"unknown scope '{part}'");
        }

        var canWrite = parts.Contains("write", StringComparer.OrdinalIgnoreCase);
        return canWrite ? "read,write" : "read";
    }

    /// <summary>
    /// Exact match, except for loopback hosts (127.0.0.1, [::1], localhost) where the port is
    /// ignored (ADR §4.2 "loopback: shoda bez portu"). The host itself still has to match
    /// exactly — 127.0.0.1 is not treated as equivalent to localhost.
    /// </summary>
    private static bool RedirectUriMatches(string requested, string allowed)
    {
        if (string.Equals(requested, allowed, StringComparison.Ordinal))
            return true;

        if (!Uri.TryCreate(requested, UriKind.Absolute, out var requestedUri) ||
            !Uri.TryCreate(allowed, UriKind.Absolute, out var allowedUri))
            return false;

        if (requestedUri.Scheme != allowedUri.Scheme)
            return false;

        if (!IsLoopbackHost(requestedUri) || !IsLoopbackHost(allowedUri))
            return false;

        return string.Equals(requestedUri.Host, allowedUri.Host, StringComparison.OrdinalIgnoreCase)
               && requestedUri.AbsolutePath == allowedUri.AbsolutePath
               && requestedUri.Query == allowedUri.Query;
    }

    private static bool IsLoopbackHost(Uri uri)
        => uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
}
