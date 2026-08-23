namespace Fakvio.Contracts.Dto.ApiKey;

/// <summary>
/// Read-model for one API key, returned by GET /api/api-key.
/// Contains no secret: only the display prefix, never the key or its hash.
/// </summary>
public class ApiKeyDto
{
    /// <summary>API key primary key.</summary>
    public long Id { get; set; }

    /// <summary>Label the user gave the key, e.g. "Claude Desktop — notebook".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>First 12 characters of the key — enough to tell keys apart, useless as a credential.</summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>Granted scopes, comma-joined lower-case ("read" or "read,write").</summary>
    public string Scopes { get; set; } = string.Empty;

    /// <summary>When the key was created (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Expiration (UTC) — null means the key never expires.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Approximate time of last use (UTC) — null means never used.</summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>When the key was revoked (UTC) — null means still valid.</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// True when the key would authenticate right now: not revoked and not expired.
    /// Computed so the UI does not have to repeat the rule.
    /// </summary>
    public bool IsActive => RevokedAt is null && (ExpiresAt is null || ExpiresAt > DateTime.UtcNow);
}
