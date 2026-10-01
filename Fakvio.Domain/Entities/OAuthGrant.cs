using Fakvio.Domain.Common;
using Fakvio.Domain.Enums;

namespace Fakvio.Domain.Entities;

/// <summary>
/// One "this MCP client may act as this user" consent, per ADR 0001
/// (docs/adr/0001-mcp-oauth21.md) §4.3.
///
/// This is the durable record of the grant: the audit trail the ADR asks for is these rows
/// (who, for which client, which scope, when, when last used, when and why revoked) — there
/// is no separate audit table (YAGNI, §4.11).
///
/// A grant owns the OAuth access tokens issued under it (as <see cref="ApiKey"/> rows with
/// <see cref="ApiKey.OAuthGrantId"/> set) and the refresh tokens that can mint new ones.
/// Revoking a grant must kill both in one write — see <c>IOAuthService.RevokeGrantAsync</c>.
/// </summary>
public class OAuthGrant : BaseEntity
{
    /// <summary>The Fakvio user who granted consent. Master schema — same reasoning as <see cref="ApiKey"/>.</summary>
    public long UserId { get; set; }

    public User User { get; set; } = null!;

    /// <summary>
    /// The client's <c>client_id</c> — a CIMD URL (e.g. <c>https://claude.ai/oauth/claude-code-client-metadata</c>).
    /// Never a self-asserted name — see ADR §4.9 on why the consent screen shows this, not <see cref="ClientName"/>.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Snapshot of the CIMD document's <c>client_name</c> at consent time, display-only.</summary>
    public string ClientName { get; set; } = string.Empty;

    /// <summary>Granted scopes, canonical form ("read" or "read,write") — same convention as <see cref="ApiKey.Scopes"/>.</summary>
    public string Scopes { get; set; } = string.Empty;

    /// <summary>The RFC 8707 resource this grant is valid for — always the MCP canonical URL in v1 (§4.4).</summary>
    public string Resource { get; set; } = string.Empty;

    /// <summary>Approximate last time an access or refresh token from this grant was used.</summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>Absolute cap on the grant's lifetime (Q4 — 180 days), independent of refresh rotation.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>Non-null once revoked — every access/refresh token under this grant stops working immediately.</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>Who revoked it — null for system-triggered reasons (reuse detection, supersession, credential change).</summary>
    public long? RevokedByUserId { get; set; }

    public EOAuthGrantRevokedReason? RevokedReason { get; set; }
}
