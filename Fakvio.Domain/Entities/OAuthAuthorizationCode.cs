using Fakvio.Domain.Common;

namespace Fakvio.Domain.Entities;

/// <summary>
/// A single-use authorization code issued at the end of the consent flow, per ADR 0001
/// (docs/adr/0001-mcp-oauth21.md) §4.2/§4.3.
///
/// Only the SHA-256 hash is ever stored (same reasoning as <see cref="ApiKey.KeyHash"/> —
/// 32 bytes of CSPRNG entropy, so a fast deterministic hash with a unique index beats a slow
/// salted one). The raw code lives only in the redirect URL back to the client, for 60 seconds.
///
/// <see cref="ConsumedAt"/> is set by an atomic
/// <c>UPDATE … SET "ConsumedAt" = now() WHERE "ConsumedAt" IS NULL</c> — see
/// <c>IOAuthService.ExchangeAuthorizationCodeAsync</c> — so two concurrent redemptions of the
/// same code can never both succeed (T3: code reuse must revoke the grant it produced).
/// </summary>
public class OAuthAuthorizationCode : BaseEntity
{
    /// <summary>Base64 of SHA-256 over the raw code. Unique index — the lookup selector.</summary>
    public string CodeHash { get; set; } = string.Empty;

    public long UserId { get; set; }

    public User User { get; set; } = null!;

    /// <summary>Bound at consent time — the token endpoint must see the identical client_id (§4.2 T3).</summary>
    public string ClientId { get; set; } = string.Empty;

    public string ClientName { get; set; } = string.Empty;

    /// <summary>Bound at authorize time — the token endpoint must see the identical redirect_uri (T2/T3).</summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>PKCE S256 challenge from the authorize request. Plain is never accepted (ADR §4.2).</summary>
    public string CodeChallenge { get; set; } = string.Empty;

    /// <summary>Scopes the user actually granted on the consent screen (may be narrower than requested).</summary>
    public string Scopes { get; set; } = string.Empty;

    public string Resource { get; set; } = string.Empty;

    /// <summary>60 seconds from issuance (ADR §4.2 "Životnosti").</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>Non-null once redeemed. A second redemption attempt is what T3 calls "code reuse".</summary>
    public DateTime? ConsumedAt { get; set; }

    /// <summary>
    /// The grant this code produced, once redeemed. Kept so a reuse attempt after redemption
    /// knows which grant to revoke (ADR §4.3) — without this, revoking on reuse would have no
    /// target because the code itself carries no scope-independent identity beyond the user.
    /// </summary>
    public long? GrantId { get; set; }
}
