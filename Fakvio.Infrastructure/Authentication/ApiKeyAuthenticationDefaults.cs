namespace Fakvio.Infrastructure.Authentication;

/// <summary>
/// Names and claim types shared by every part of the API-key authentication path:
/// the ASP.NET Core handler (API host), the mirror middleware (Functions host) and
/// the scope guard used by both.
///
/// They live in one place because a typo in any of them fails *open-ish* in the worst
/// way: a scope claim nobody can find looks exactly like a key with no write scope
/// in one place and like an unrestricted key in another.
/// </summary>
public static class ApiKeyAuthenticationDefaults
{
    /// <summary>
    /// Name of the ASP.NET Core authentication scheme that validates API keys.
    /// </summary>
    public const string AuthenticationScheme = "ApiKey";

    /// <summary>
    /// Name of the policy scheme that looks at the Bearer token and forwards to either
    /// <see cref="AuthenticationScheme"/> or JWT Bearer. It is the host's default
    /// authenticate/challenge scheme, so callers never pick a scheme explicitly.
    /// </summary>
    public const string SelectorScheme = "FakvioBearer";

    /// <summary>
    /// Every raw API key starts with this (see <c>ApiKeyService</c>). It is what tells
    /// an API key apart from a JWT, which always starts with "eyJ" (Base64 of '{"').
    /// </summary>
    public const string RawKeyPrefix = "fak_";

    /// <summary>
    /// Claim holding the granted scopes of the key that authenticated the request
    /// ("read" or "read,write"). Absent on JWT-authenticated requests — that absence
    /// is exactly how the scope guard knows to leave a JWT request alone.
    /// </summary>
    public const string ScopeClaimType = "api_key_scope";

    /// <summary>
    /// Claim holding the id of the authenticating <c>ApiKey</c> row. For diagnostics
    /// and for <c>GET /api/api-key/me</c>; never used as a permission input.
    /// </summary>
    public const string KeyIdClaimType = "api_key_id";

    /// <summary>
    /// Present only when the authenticating credential is an OAuth-issued access token
    /// (ADR 0001, docs/adr/0001-mcp-oauth21.md §4.3/§4.5/§4.7) — i.e. the underlying
    /// <c>ApiKey.OAuthGrantId</c> is not null. Used to: (a) block OAuth principals from
    /// impersonating a company via X-Company-Id (<c>ImpersonationMiddleware</c>), and
    /// (b) block them from <c>/api/api-key</c> and <c>/api/oauth/grants</c> management
    /// endpoints, same as any other API-key-scoped principal.
    /// </summary>
    public const string OAuthGrantIdClaimType = "oauth_grant_id";

    /// <summary>
    /// The RFC 8707 <c>resource</c> the authenticating OAuth grant was issued for. Only present
    /// alongside <see cref="OAuthGrantIdClaimType"/>. <c>GET /api/api-key/me</c> returns it so
    /// the MCP gate (§4.4/§4.6 in the ADR) can refuse a token whose resource is not its own
    /// canonical URL — preparation for a future second resource server.
    /// </summary>
    public const string OAuthResourceClaimType = "oauth_resource";

    /// <summary>
    /// Internal header the MCP host adds to every request it forwards to the API (ADR 0001
    /// §4.4). Its value is the shared secret <c>McpOAuth:ResourceProofSecret</c> — proof that
    /// the caller is the MCP host, not the raw internet. Only checked for OAuth-issued tokens.
    /// </summary>
    public const string ResourceProofHeaderName = "X-Fakvio-Resource-Proof";

    /// <summary>
    /// The scope required to perform a state-changing request. Lower case because that
    /// is the canonical stored form (<c>EApiKeyScope</c> persisted by <c>ApiKeyService</c>).
    /// </summary>
    public const string WriteScope = "write";

    /// <summary>
    /// Returns the raw API key carried by the header value, or null when the header is
    /// missing, is not a Bearer header, or carries something that is not an API key.
    ///
    /// Shared by the scheme selector, the API handler and the Functions middleware so
    /// that all three agree on what "this request presents an API key" means.
    /// </summary>
    public static string? ExtractRawKey(string? authorizationHeader)
    {
        const string bearer = "Bearer ";

        if (string.IsNullOrWhiteSpace(authorizationHeader)
            || !authorizationHeader.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
            return null;

        var token = authorizationHeader[bearer.Length..].Trim();

        return token.StartsWith(RawKeyPrefix, StringComparison.Ordinal) ? token : null;
    }
}
