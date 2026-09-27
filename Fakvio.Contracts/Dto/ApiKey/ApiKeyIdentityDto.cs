namespace Fakvio.Contracts.Dto.ApiKey;

/// <summary>
/// What the current credential authenticates as, returned by GET /api/api-key/me.
/// A machine client (MCP server, CI job) calls it to check that its key still works and
/// to learn what it may do, without having to make a real business request first.
///
/// Reflects the principal, so it answers for a JWT session too — the API-key specific
/// fields are simply null there.
/// </summary>
public class ApiKeyIdentityDto
{
    /// <summary>Id of the authenticated user.</summary>
    public long UserId { get; set; }

    /// <summary>Email of the authenticated user.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Display name of the authenticated user.</summary>
    public string FullName { get; set; } = string.Empty;

    /// <summary>Role name (User, Admin, SysAdmin…) — the ceiling on what the credential can do.</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// Tenant the request runs against, or null for a SysAdmin who is not impersonating.
    /// Reflects impersonation: with X-Company-Id set, this is the impersonated company.
    /// </summary>
    public long? CompanyId { get; set; }

    /// <summary>
    /// Granted scopes ("read" or "read,write") when an API key authenticated the request;
    /// null for a JWT session, which is not scope-limited.
    /// </summary>
    public string? Scopes { get; set; }

    /// <summary>Id of the authenticating API key, or null for a JWT session.</summary>
    public long? ApiKeyId { get; set; }

    /// <summary>
    /// Id of the OAuth grant behind this credential (ADR 0001, docs/adr/0001-mcp-oauth21.md
    /// §4.3), or null for a manually created API key or a JWT session.
    /// </summary>
    public long? OAuthGrantId { get; set; }

    /// <summary>
    /// The RFC 8707 resource this OAuth access token was issued for, or null when the
    /// credential is not an OAuth token. The MCP host's gate refuses a token whose resource is
    /// not its own canonical URL (ADR §4.4/§4.6) — preparation for a future second resource server.
    /// </summary>
    public string? OAuthResource { get; set; }
}
