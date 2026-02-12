using InvoiceApi.Domain.Enums;

namespace InvoiceApi.Application.Dto.Auth;

/// <summary>
/// DTO for login response containing JWT token and user info
/// </summary>
public class LoginResponse
{
    /// <summary>
    /// JWT access token
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Token expiration time
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// User ID
    /// </summary>
    public long UserId { get; set; }

    /// <summary>
    /// User's email
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// User's full name
    /// </summary>
    public string FullName { get; set; } = string.Empty;

    /// <summary>
    /// User's role
    /// </summary>
    public EUserRole Role { get; set; }

    /// <summary>
    /// Company ID (null for SysAdmin)
    /// </summary>
    public long? CompanyId { get; set; }

    /// <summary>
    /// Company name (null for SysAdmin)
    /// </summary>
    public string? CompanyName { get; set; }

    /// <summary>
    /// Whether this user authenticates via an external OAuth provider (Google, Microsoft, etc.)
    /// When true, password change and password login are not available.
    /// </summary>
    public bool IsExternalLogin { get; set; }

    // ─── Two-Factor Authentication ─────────────────────────────────────────

    /// <summary>
    /// When true, the login is not complete — the user must provide a 2FA code
    /// in a second step. Token will be empty in this case.
    /// </summary>
    public bool RequiresTwoFactor { get; set; }

    /// <summary>
    /// Encrypted session token for the 2FA verification step.
    /// Only set when RequiresTwoFactor == true.
    /// Pass this to the /api/twofactor/verify endpoint along with the 6-digit code.
    /// </summary>
    public string? TwoFactorSessionToken { get; set; }

    /// <summary>
    /// The 2FA method the user has configured (Totp or Email).
    /// Used by the UI to show the appropriate input instructions.
    /// Only set when RequiresTwoFactor == true.
    /// </summary>
    public ETwoFactorMethod? TwoFactorMethod { get; set; }
}
