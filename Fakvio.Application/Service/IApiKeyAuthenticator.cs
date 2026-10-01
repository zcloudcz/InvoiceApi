using System.Security.Claims;

namespace Fakvio.Application.Service;

/// <summary>
/// Turns a raw API key into an authenticated principal. The single place where a key
/// is validated, shared by both hosting models (see CLAUDE.md — "API + Functions
/// duplication"): the API host wraps it in an <c>AuthenticationHandler</c>, the
/// Functions host in a worker middleware. Neither driver contains any validation logic.
/// </summary>
public interface IApiKeyAuthenticator
{
    /// <summary>
    /// Validates <paramref name="rawKey"/> and builds the principal the request runs as.
    ///
    /// Returns null — and the caller must fail the request closed — when the key is
    /// unknown, revoked, expired, or belongs to a deactivated user. The reason is never
    /// returned to the caller: telling an attacker "revoked" versus "unknown" only helps
    /// them, and every case ends as the same 401.
    ///
    /// The principal carries exactly the claims the JWT path produces
    /// (<c>AuthService.GenerateJwtTokenAsync</c>) plus the key's scopes, so impersonation,
    /// tenant resolution and <c>[Authorize(Roles = …)]</c> behave identically.
    /// </summary>
    /// <param name="rawKey">The bearer credential (an API key or an OAuth access token).</param>
    /// <param name="resourceProofHeader">
    /// Value of the internal <c>X-Fakvio-Resource-Proof</c> header (ADR 0001,
    /// docs/adr/0001-mcp-oauth21.md §4.4), added only by the MCP host. Required, and checked in
    /// constant time, when <paramref name="rawKey"/> resolves to an OAuth-issued access token;
    /// ignored for a manually created key.
    /// </param>
    Task<ClaimsPrincipal?> AuthenticateAsync(string rawKey, string? resourceProofHeader = null, CancellationToken ct = default);
}
