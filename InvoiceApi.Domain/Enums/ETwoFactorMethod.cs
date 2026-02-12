namespace InvoiceApi.Domain.Enums;

/// <summary>
/// Defines the method used for Two-Factor Authentication (2FA).
/// Users can choose between an authenticator app (TOTP) or email-based OTP codes.
/// None means 2FA is disabled for this user.
/// </summary>
public enum ETwoFactorMethod
{
    /// <summary>
    /// 2FA is disabled — user logs in with password only.
    /// </summary>
    None = 0,

    /// <summary>
    /// Time-based One-Time Password (TOTP) — authenticator app like Google Authenticator or Authy.
    /// The user scans a QR code during setup and enters 6-digit codes from the app on each login.
    /// </summary>
    Totp = 1,

    /// <summary>
    /// Email-based One-Time Password — a 6-digit code is sent to the user's email on each login.
    /// Simpler setup but requires reliable email delivery.
    /// </summary>
    Email = 2
}
