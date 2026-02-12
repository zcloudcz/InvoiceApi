namespace InvoiceApi.BlazorUI.Models;

/// <summary>
/// Blazor model for TOTP setup response from the API.
/// Contains the QR code image, manual entry key, and otpauth:// URI
/// needed for the user to configure their authenticator app.
/// </summary>
public class TotpSetupResponse
{
    /// <summary>
    /// QR code image as PNG bytes. Display as base64-encoded img src in the UI.
    /// </summary>
    public byte[] QrCodeImage { get; set; } = [];

    /// <summary>
    /// Base32-encoded TOTP secret for manual entry into the authenticator app.
    /// </summary>
    public string ManualEntryKey { get; set; } = string.Empty;

    /// <summary>
    /// Full otpauth:// URI (encoded in the QR code).
    /// </summary>
    public string OtpAuthUri { get; set; } = string.Empty;
}
