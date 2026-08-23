using System.ComponentModel.DataAnnotations;

namespace Fakvio.Contracts.Dto.ApiKey;

/// <summary>
/// Request body of POST /api/api-key.
/// </summary>
public class CreateApiKeyDto
{
    /// <summary>
    /// Label shown in the key list. Required, trimmed, max 100 characters.
    /// </summary>
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Requested scopes, comma-joined and case-insensitive: "read" or "read,write".
    /// "write" alone is accepted and normalized to "read,write" — write without read
    /// would be a credential nobody can use.
    /// </summary>
    [Required]
    public string Scopes { get; set; } = "read";

    /// <summary>
    /// Optional expiration (UTC). Must be in the future. Null = never expires.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }
}
