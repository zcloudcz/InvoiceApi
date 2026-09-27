namespace Fakvio.Domain.Enums;

/// <summary>
/// Why an <see cref="Entities.OAuthGrant"/> was revoked. Kept as a small closed set
/// (not a free-text string) so the "Připojené aplikace" list and security dashboards can
/// group and reason about revocations instead of pattern-matching a sentence.
/// See ADR 0001 (docs/adr/0001-mcp-oauth21.md) §4.3 and §4.11.
/// </summary>
public enum EOAuthGrantRevokedReason
{
    /// <summary>The user clicked "Odebrat" on the Integrations page.</summary>
    User = 0,

    /// <summary>An already-consumed refresh token was presented again (§4.2, T5) — the whole grant is revoked.</summary>
    RefreshReuse = 1,

    /// <summary>An already-consumed authorization code was presented again (§4.2, T3).</summary>
    CodeReuse = 2,

    /// <summary>A SysAdmin revoked the grant (not exposed via API yet — reserved for future admin tooling).</summary>
    Admin = 3,

    /// <summary>Replaced by a newer grant for the same user + client_id + resource (§4.3 — "Reconnect" must not pile up rows).</summary>
    Superseded = 4,

    /// <summary>The user changed or reset their password, or turned off 2FA (Q6 — a credential change revokes all OAuth grants).</summary>
    CredentialChanged = 5
}
