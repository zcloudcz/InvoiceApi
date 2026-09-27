using Fakvio.Domain.Common;

namespace Fakvio.Domain.Entities;

/// <summary>
/// A rotating refresh token belonging to an <see cref="OAuthGrant"/>, per ADR 0001
/// (docs/adr/0001-mcp-oauth21.md) §4.2/§4.3.
///
/// Only the hash is stored (same reasoning as <see cref="ApiKey.KeyHash"/>). Each successful
/// refresh consumes the presented token (<see cref="ConsumedAt"/>) and inserts a new row in
/// the same grant — the "family" the ADR's reuse detection talks about is simply
/// "all <see cref="OAuthRefreshToken"/> rows sharing a <see cref="GrantId"/>".
///
/// Owner decision Q5 (ADR §9): a second use of the SAME token within 10 seconds of its own
/// rotation is treated as a benign concurrent-refresh race (Claude refreshing from two
/// requests at once) and returns <c>invalid_grant</c> WITHOUT revoking the grant. Reuse after
/// that window is treated as theft and revokes the whole grant. See
/// <c>IOAuthService.RefreshAsync</c> for where <see cref="ConsumedAt"/> is compared against
/// that window.
/// </summary>
public class OAuthRefreshToken : BaseEntity
{
    /// <summary>Base64 of SHA-256 over the raw refresh token. Unique index — the lookup selector.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public long GrantId { get; set; }

    public OAuthGrant Grant { get; set; } = null!;

    /// <summary>30 days, sliding — each rotation resets this (ADR §4.2, subject to the grant's absolute cap).</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Non-null once used to mint a new access/refresh token pair. Kept around for a short
    /// while after consumption (not deleted immediately) specifically so a reuse attempt has
    /// something to detect — see <see cref="OAuthCleanupService"/> for how long.
    /// </summary>
    public DateTime? ConsumedAt { get; set; }
}
