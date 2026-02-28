namespace InvoiceApi.UI.Shared.Models;

/// <summary>
/// Model for login response from API
/// </summary>
public class LoginResponse
{
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public long UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public int Role { get; set; } // 0 = User, 1 = Admin, 2 = SysAdmin
    public long? CompanyId { get; set; }
    public string? CompanyName { get; set; }

    /// <summary>
    /// Whether this user authenticates via an external OAuth provider.
    /// When true, password-related features are hidden in the UI.
    /// </summary>
    public bool IsExternalLogin { get; set; }

    // ─── Two-Factor Authentication ─────────────────────────────────────────

    /// <summary>
    /// When true, the user must provide a 2FA code in a second step.
    /// Token will be empty until the code is verified.
    /// </summary>
    public bool RequiresTwoFactor { get; set; }

    /// <summary>
    /// Encrypted session token for the 2FA verification step.
    /// Only set when RequiresTwoFactor == true.
    /// </summary>
    public string? TwoFactorSessionToken { get; set; }

    /// <summary>
    /// The 2FA method as an integer (1 = TOTP, 2 = Email).
    /// Used by the UI to show method-specific instructions.
    /// </summary>
    public int? TwoFactorMethod { get; set; }
}
