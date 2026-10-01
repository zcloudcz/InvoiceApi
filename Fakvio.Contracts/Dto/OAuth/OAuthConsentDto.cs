namespace Fakvio.Contracts.Dto.OAuth;

/// <summary>
/// Consent-screen display data for <c>GET /api/oauth/consent/{ticket}</c> (ADR 0001,
/// docs/adr/0001-mcp-oauth21.md §4.2/§4.9).
/// </summary>
public class OAuthConsentInfoDto
{
    /// <summary>The client_id URL — the PRIMARY identity shown on the consent screen (never <see cref="ClientName"/> alone).</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Self-asserted display name from the CIMD document — subtitle only, never trusted as identity.</summary>
    public string ClientName { get; set; } = string.Empty;

    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>True when the client_id's host is on <c>McpOAuth:TrustedClientHosts</c> — shown as an "ověřená aplikace" badge.</summary>
    public bool IsTrustedClient { get; set; }

    /// <summary>True when the redirect_uri is a loopback address — shown as a "runs on your computer" warning (T10).</summary>
    public bool IsLoopbackRedirect { get; set; }

    /// <summary>Scopes requested at /oauth/authorize ("read" or "read,write") — the user may narrow this, never widen it.</summary>
    public string RequestedScopes { get; set; } = string.Empty;

    public string UserEmail { get; set; } = string.Empty;

    public string? CompanyName { get; set; }

    /// <summary>The company displayed to the user; echoed when consenting.</summary>
    public long? CompanyId { get; set; }

    /// <summary>False during closed early access for a user not on the allowlist (ADR §5.2) — the UI shows a "closed beta" message instead of Allow/Deny.</summary>
    public bool IsEligible { get; set; }
}

/// <summary>Body of <c>POST /api/oauth/consent/decision</c>.</summary>
public class OAuthConsentDecisionDto
{
    public string Ticket { get; set; } = string.Empty;

    public bool Allow { get; set; }

    /// <summary>Must match the company displayed and the current interactive session.</summary>
    public long? CompanyId { get; set; }

    /// <summary>Required when <see cref="Allow"/> is true — "read" or "read,write", must be a subset of what was requested.</summary>
    public string? Scope { get; set; }
}

/// <summary>Response of <c>POST /api/oauth/consent/decision</c> — the URL the WASM page navigates to next.</summary>
public class OAuthConsentDecisionResultDto
{
    public string RedirectUrl { get; set; } = string.Empty;
}
