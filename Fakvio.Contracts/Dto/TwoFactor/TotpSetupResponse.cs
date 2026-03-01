namespace Fakvio.Contracts.Dto.TwoFactor;

/// <summary>
/// DTO returned when a user initiates TOTP (authenticator app) setup.
/// Contains everything the user needs to configure their authenticator app:
/// - QR code image (PNG bytes for scanning)
/// - Manual entry key (for typing into the app)
/// - Full otpauth:// URI (the raw data encoded in the QR code)
/// </summary>
public class TotpSetupResponse
{
    /// <summary>
    /// QR code image as PNG bytes. Display in the UI as base64-encoded img src.
    /// The user scans this with their authenticator app (Google Authenticator, Authy, etc.)
    /// </summary>
    public byte[] QrCodeImage { get; set; } = [];

    /// <summary>
    /// Base32-encoded TOTP secret key for manual entry.
    /// Users who can't scan the QR code can type this into their app.
    /// </summary>
    public string ManualEntryKey { get; set; } = string.Empty;

    /// <summary>
    /// Full otpauth:// URI containing issuer, account name, and secret.
    /// This is what's encoded in the QR code.
    /// Format: otpauth://totp/AppName:user@email.com?secret=BASE32KEY&issuer=AppName
    /// </summary>
    public string OtpAuthUri { get; set; } = string.Empty;
}
