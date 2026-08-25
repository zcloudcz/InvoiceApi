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
