using System.ComponentModel.DataAnnotations;

namespace InvoiceApi.Application.Dto.TwoFactor;

/// <summary>
/// DTO for verifying the TOTP code during initial setup.
/// The user enters the 6-digit code from their authenticator app to confirm
/// they've successfully scanned the QR code. Only then is TOTP enabled.
/// </summary>
public class VerifyTotpSetupRequest
{
    /// <summary>
    /// 6-digit code from the user's authenticator app.
    /// Must match the current TOTP window to confirm successful setup.
    /// </summary>
    [Required]
    [RegularExpression(@"^\d{6}$", ErrorMessage = "Code must be exactly 6 digits.")]
    public string Code { get; set; } = string.Empty;
}
