using System.ComponentModel.DataAnnotations;

namespace Fakvio.Contracts.Dto.TwoFactor;

/// <summary>
/// DTO for the second step of the login flow — verifying the 2FA code.
/// After successful password authentication, the user receives a session token
/// and must provide a 6-digit code (from authenticator app or email).
/// </summary>
public class VerifyTwoFactorRequest
{
    /// <summary>
    /// Encrypted session token issued after successful password verification.
    /// Links this code verification to the original login attempt.
    /// </summary>
    [Required]
    public string SessionToken { get; set; } = string.Empty;

    /// <summary>
    /// 6-digit verification code — either from the authenticator app (TOTP)
    /// or from the email that was sent (email OTP).
    /// </summary>
    [Required]
    [RegularExpression(@"^\d{6}$", ErrorMessage = "Code must be exactly 6 digits.")]
    public string Code { get; set; } = string.Empty;
}
