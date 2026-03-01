using System.ComponentModel.DataAnnotations;

namespace Fakvio.Contracts.Dto.TwoFactor;

/// <summary>
/// DTO for disabling Two-Factor Authentication.
/// The user must confirm their password to prevent unauthorized disabling
/// (e.g., if someone has access to an already-logged-in session).
/// </summary>
public class DisableTwoFactorRequest
{
    /// <summary>
    /// The user's current password for confirmation.
    /// Required to prove identity before disabling 2FA.
    /// </summary>
    [Required]
    public string Password { get; set; } = string.Empty;
}
