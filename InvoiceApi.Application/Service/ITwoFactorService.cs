using InvoiceApi.Application.Dto.TwoFactor;

namespace InvoiceApi.Application.Service;

/// <summary>
/// Service interface for Two-Factor Authentication (2FA) operations.
/// Supports TOTP (authenticator app) and Email (OTP code) methods.
///
/// Typical flow:
/// 1. User enables 2FA via InitiateTotpSetupAsync/VerifyTotpSetupAsync or EnableEmailTwoFactorAsync
/// 2. On next login, AuthService calls GenerateTwoFactorSessionTokenAsync after password verification
/// 3. User enters 6-digit code, frontend calls VerifyTwoFactorCodeAsync with session token + code
/// 4. If valid, returns userId for JWT generation
///
/// Admin can force-disable 2FA for locked-out users via ForceDisableTwoFactorAsync.
/// </summary>
public interface ITwoFactorService
{
    /// <summary>
    /// Initiates TOTP setup — generates a new secret, QR code image, and manual entry key.
    /// The secret is temporarily stored (encrypted) but 2FA is NOT enabled yet.
    /// The user must call VerifyTotpSetupAsync with a valid code to finalize setup.
    /// </summary>
    /// <param name="userId">ID of the user setting up TOTP</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>QR code image, manual key, and otpauth:// URI for the authenticator app</returns>
    Task<TotpSetupResponse> InitiateTotpSetupAsync(long userId, CancellationToken ct = default);

    /// <summary>
    /// Verifies the TOTP code during setup — confirms the user has scanned the QR code.
    /// On success: enables 2FA with TOTP method.
    /// On failure: returns false (user can retry with a new code).
    /// </summary>
    /// <param name="userId">ID of the user verifying TOTP setup</param>
    /// <param name="code">6-digit code from the authenticator app</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if the code is valid and 2FA is now enabled</returns>
    Task<bool> VerifyTotpSetupAsync(long userId, string code, CancellationToken ct = default);

    /// <summary>
    /// Enables email-based 2FA for the user. No setup verification needed —
    /// just sets the method to Email. Requires a verified email address.
    /// </summary>
    /// <param name="userId">ID of the user enabling email 2FA</param>
    /// <param name="ct">Cancellation token</param>
    Task EnableEmailTwoFactorAsync(long userId, CancellationToken ct = default);

    /// <summary>
    /// Generates an encrypted session token for the 2FA verification step.
    /// Called by AuthService after successful password verification.
    /// For email method: also generates and sends a 6-digit OTP code via email.
    /// </summary>
    /// <param name="userId">ID of the user logging in</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Encrypted session token to pass to the frontend</returns>
    Task<string> GenerateTwoFactorSessionTokenAsync(long userId, CancellationToken ct = default);

    /// <summary>
    /// Verifies a 2FA code during login (step 2).
    /// Validates the session token, checks the code (TOTP or email OTP),
    /// and enforces rate limiting (max 5 failed attempts).
    /// </summary>
    /// <param name="sessionToken">Encrypted session token from step 1</param>
    /// <param name="code">6-digit verification code</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>User ID if verification succeeds; null if invalid/expired/rate-limited</returns>
    Task<long?> VerifyTwoFactorCodeAsync(string sessionToken, string code, CancellationToken ct = default);

    /// <summary>
    /// Disables 2FA for a user. Called by the user themselves.
    /// Clears all 2FA-related fields (secret, codes, session tokens).
    /// </summary>
    /// <param name="userId">ID of the user disabling their own 2FA</param>
    /// <param name="ct">Cancellation token</param>
    Task DisableTwoFactorAsync(long userId, CancellationToken ct = default);

    /// <summary>
    /// Force-disables 2FA for a user. Called by Admin/SysAdmin when a user is locked out.
    /// No password verification required — admin authority is sufficient.
    /// </summary>
    /// <param name="userId">ID of the user whose 2FA should be disabled</param>
    /// <param name="adminUserId">ID of the admin performing the action (for audit logging)</param>
    /// <param name="ct">Cancellation token</param>
    Task ForceDisableTwoFactorAsync(long userId, long adminUserId, CancellationToken ct = default);

    /// <summary>
    /// Gets the current 2FA status for a user.
    /// Returns whether 2FA is enabled, the method, and when it was enabled.
    /// </summary>
    /// <param name="userId">ID of the user to check</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>2FA status details</returns>
    Task<TwoFactorStatusDto> GetTwoFactorStatusAsync(long userId, CancellationToken ct = default);
}
