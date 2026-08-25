using Fakvio.Domain.Common;

namespace Fakvio.Domain.Entities;

/// <summary>
/// A long-lived, revocable API credential belonging to a single user.
/// Used by machine clients (Fakvio MCP server, curl, CI) instead of the
/// 24-hour user JWT.
///
/// Why the MASTER schema and not a tenant schema?
/// TenantContextMiddleware picks the tenant schema from the CompanyId claim,
/// which authentication has to produce first. A key stored in a tenant schema
/// would be circular: you would need the schema to find the key, and the key
/// to find the schema. The user already lives in master, and the key is the
/// user's credential, so it shares the user's lifetime and gets FK cascade for
/// free. Same reasoning as UserPreferences.
///
/// Deliberately NO CompanyId column: the tenant is derived from User.CompanyId
/// at authentication time, so moving a user between companies moves their keys
/// with them instead of leaving a stale binding behind.
/// </summary>
public class ApiKey : BaseEntity
{
    /// <summary>
    /// FK to the owning user (master DB). Cascade delete — deleting the user
    /// removes their keys.
    /// </summary>
    public long UserId { get; set; }

    /// <summary>
    /// Navigation to the owning user.
    /// </summary>
    public User User { get; set; } = null!;

    /// <summary>
    /// Human-readable label chosen by the user, e.g. "Claude Desktop — notebook".
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// First 12 characters of the raw key. For display in the key list and for
    /// correlating log entries only — NEVER used as a lookup selector, because
    /// it is not unique and not secret.
    /// </summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Base64 of SHA-256 over the raw key. The raw key itself is shown to the
    /// user exactly once at creation and never stored.
    /// </summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>
    /// Granted scopes, comma-joined lower-case (<c>"read"</c> or <c>"read,write"</c>).
    /// Stored as a string rather than a flags enum to stay consistent with how the
    /// rest of the repo persists enums.
    /// </summary>
    public string Scopes { get; set; } = string.Empty;

    /// <summary>
    /// Optional expiration. Null = never expires.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Approximate time of last successful authentication with this key.
    /// Coarse on purpose (updated at most every few minutes) so that a busy MCP
    /// session does not turn every request into a master DB write.
    /// </summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// Soft revocation timestamp. Non-null = the key no longer authenticates.
    /// Rows are kept so the audit trail survives revocation.
    /// </summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// Who revoked the key (the owner today; an admin once admin revocation exists).
    /// </summary>
    public long? RevokedByUserId { get; set; }
}
