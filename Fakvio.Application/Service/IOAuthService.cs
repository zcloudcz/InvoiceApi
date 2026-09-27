using Fakvio.Domain.Enums;

namespace Fakvio.Application.Service;

/// <summary>Successful token endpoint response (ADR 0001 §4.2 "Token"). Raw secrets, returned exactly once.</summary>
public sealed record OAuthTokenResult(string AccessToken, string RefreshToken, int ExpiresIn, string Scope);

/// <summary>Input to <see cref="IOAuthService.IssueAuthorizationCodeAsync"/> — everything the consent decision already validated.</summary>
public sealed record IssueAuthorizationCodeRequest(
    long UserId, string ClientId, string ClientName, string RedirectUri, string CodeChallenge, string Scopes, string Resource);

/// <summary>Input to <see cref="IOAuthService.ExchangeAuthorizationCodeAsync"/> — the token endpoint's <c>grant_type=authorization_code</c> request.</summary>
public sealed record ExchangeAuthorizationCodeRequest(string Code, string RedirectUri, string ClientId, string CodeVerifier, string? Resource);

/// <summary>Input to <see cref="IOAuthService.RefreshAsync"/> — the token endpoint's <c>grant_type=refresh_token</c> request.</summary>
public sealed record RefreshTokenRequest(string RefreshToken, string? Scope, string? Resource);

/// <summary>Everything the consent screen needs to know about the signed-in user (ADR §4.9).</summary>
public sealed record OAuthConsentUserInfo(string Email, string FullName, string? CompanyName, bool IsEligible);

/// <summary>
/// The OAuth 2.1 authorization server core: authorization-code issuance/exchange, refresh
/// rotation with reuse detection, and revocation. Per ADR 0001
/// (docs/adr/0001-mcp-oauth21.md) §4.2/§4.3.
///
/// Every rejection throws <see cref="Fakvio.Application.Exceptions.OAuthErrorException"/> with
/// an RFC 6749 error code and a server-only diagnostic message — see that type's docs for why.
/// </summary>
public interface IOAuthService
{
    /// <summary>
    /// Issues a single-use authorization code after the user has granted consent. Called by the
    /// consent decision endpoint (N5.4) — never directly by an OAuth client.
    /// </summary>
    Task<string> IssueAuthorizationCodeAsync(IssueAuthorizationCodeRequest request, CancellationToken ct = default);

    /// <summary>
    /// Redeems a single-use authorization code for a new grant + token pair
    /// (<c>grant_type=authorization_code</c>). Validates PKCE (S256 only), the exact
    /// redirect_uri/client_id/resource bound at consent time, and expiry. A code presented twice
    /// is treated as theft (T3): the grant it produced (if any) is revoked.
    /// </summary>
    Task<OAuthTokenResult> ExchangeAuthorizationCodeAsync(ExchangeAuthorizationCodeRequest request, CancellationToken ct = default);

    /// <summary>
    /// Rotates a refresh token (<c>grant_type=refresh_token</c>). See the ADR's Q5 owner decision
    /// for the 10-second concurrent-refresh grace window this implements, and T5 for why reuse
    /// after that window revokes the entire grant.
    /// </summary>
    Task<OAuthTokenResult> RefreshAsync(RefreshTokenRequest request, CancellationToken ct = default);

    /// <summary>
    /// RFC 7009 revocation. A refresh token revokes its whole grant; an access token revokes only
    /// itself. An unknown token is NOT an error — it returns silently, per RFC 7009 §2.2.
    /// </summary>
    Task RevokeAsync(string token, string? tokenTypeHint, CancellationToken ct = default);

    /// <summary>
    /// Revokes a grant and, in the SAME operation, every access token issued under it (ADR §4.3:
    /// "revokace grantu... a všech jeho ApiKey řádků najednou → další MCP request s tokenem
    /// vrátí 401 okamžitě"). Ownership is the caller's responsibility — this method does not
    /// check who is asking. Used for: code/refresh reuse (theft), grant supersession on
    /// reconnect, and the user-initiated "Odebrat" action (N5.7).
    /// </summary>
    Task RevokeGrantAsync(long grantId, EOAuthGrantRevokedReason reason, long? revokedByUserId, CancellationToken ct = default);

    /// <summary>
    /// Revokes every non-revoked grant of a user (Q6 — password change/reset or 2FA disable
    /// revokes all of that user's OAuth grants; API keys are untouched).
    /// </summary>
    Task RevokeAllGrantsForUserAsync(long userId, EOAuthGrantRevokedReason reason, CancellationToken ct = default);

    /// <summary>
    /// Consent-screen display data + the eligibility check (ADR §5.1 allowlist, Q9 SysAdmin)
    /// evaluated fresh — the consent screen must reflect the CURRENT allowlist state, not
    /// whatever it was when the authorize redirect happened.
    /// </summary>
    Task<OAuthConsentUserInfo> GetConsentUserInfoAsync(long userId, CancellationToken ct = default);

    /// <summary>
    /// Pure validation helper shared with the authorize endpoint (N5.4): missing/empty →
    /// the canonical resource (<c>McpOAuth:Resource</c>); present but different → throws
    /// <see cref="Fakvio.Application.Exceptions.OAuthErrorException"/> with <c>invalid_target</c>
    /// (ADR §4.2 "chybí → dosadit canonical; jiná hodnota → invalid_target").
    /// </summary>
    string ResolveCanonicalResource(string? requestedResource);
}
