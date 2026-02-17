using InvoiceApi.Contracts.Dto.TwoFactor;
using InvoiceApi.Application.Service;
using InvoiceApi.Domain.Enums;
using InvoiceApi.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OtpNet;
using QRCoder;
using System.Security.Cryptography;

namespace InvoiceApi.Infrastructure.Service;

/// <summary>
/// Service implementing Two-Factor Authentication (2FA) for TOTP and Email methods.
///
/// Security measures:
/// - TOTP secrets are encrypted at rest using ASP.NET Core Data Protection API
/// - Email OTP codes are BCrypt-hashed before storage (same as passwords)
/// - Session tokens are encrypted and time-limited (5 minutes)
/// - Rate limiting: max 5 failed attempts per session, then session is invalidated
///
/// Dependencies:
/// - MasterDbContext: User table (all users are in master DB)
/// - IDataProtectionProvider: encrypts/decrypts TOTP secrets and session tokens
/// - IEmailService: sends OTP codes via email
/// - IContentTemplateService: resolves email template for 2FA codes
/// - IConfiguration: reads app name for QR code labels
/// </summary>
public class TwoFactorService : ITwoFactorService
{
    private readonly MasterDbContext _context;
    private readonly IDataProtector _protector;
    private readonly IEmailService _emailService;
    private readonly IContentTemplateService _contentTemplateService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TwoFactorService> _logger;

    // Data Protection purposes — unique string scopes for different encryption needs
    private const string TotpSecretPurpose = "TwoFactorAuth.TotpSecret";
    private const string SessionTokenPurpose = "TwoFactorAuth.SessionToken";

    // Security constants
    private const int TotpSecretSizeBytes = 20;      // 160-bit secret (RFC 4226 recommendation)
    private const int EmailCodeLength = 6;             // 6-digit OTP code
    private const int EmailCodeExpirationMinutes = 5;  // Code valid for 5 minutes
    private const int SessionTokenExpirationMinutes = 5; // Session valid for 5 minutes
    private const int MaxFailedAttempts = 5;           // Lockout after 5 wrong codes

    public TwoFactorService(
        MasterDbContext context,
        IDataProtectionProvider dataProtectionProvider,
        IEmailService emailService,
        IContentTemplateService contentTemplateService,
        IConfiguration configuration,
        ILogger<TwoFactorService> logger)
    {
        _context = context;
        // Create a scoped protector — each purpose produces different encrypted outputs
        _protector = dataProtectionProvider.CreateProtector(TotpSecretPurpose);
        _emailService = emailService;
        _contentTemplateService = contentTemplateService;
        _configuration = configuration;
        _logger = logger;
    }

    // ─── TOTP Setup ──────────────────────────────────────────────────────────

    /// <summary>
    /// Step 1 of TOTP setup: generates a new secret, QR code, and manual key.
    /// The secret is stored encrypted in the database but 2FA is NOT enabled yet.
    /// The user must verify a code from their app to complete setup.
    /// </summary>
    public async Task<TotpSetupResponse> InitiateTotpSetupAsync(long userId, CancellationToken ct = default)
    {
        var user = await _context.User.FindAsync([userId], ct)
            ?? throw new InvalidOperationException($"User with ID {userId} not found.");

        // Generate a cryptographically secure random secret (20 bytes = 160 bits)
        var secretBytes = RandomNumberGenerator.GetBytes(TotpSecretSizeBytes);
        var base32Secret = Base32Encoding.ToString(secretBytes);

        // Encrypt the secret before storing it in the database.
        // Data Protection API uses machine-specific keys, so the encrypted value
        // can only be decrypted on the same server (or with shared key storage).
        user.TotpSecretEncrypted = _protector.Protect(base32Secret);
        await _context.SaveChangesAsync(ct);

        // Build the otpauth:// URI for the QR code
        // Format: otpauth://totp/AppName:user@email.com?secret=BASE32KEY&issuer=AppName
        var appName = _configuration["AppSettings:Name"] ?? "InvoiceApi";
        var otpAuthUri = $"otpauth://totp/{Uri.EscapeDataString(appName)}:{Uri.EscapeDataString(user.Email)}" +
                         $"?secret={base32Secret}&issuer={Uri.EscapeDataString(appName)}&digits=6&period=30";

        // Generate QR code PNG image using QRCoder
        // PngByteQRCode works server-side without System.Drawing (unlike QRCode)
        var qrGenerator = new QRCodeGenerator();
        var qrCodeData = qrGenerator.CreateQrCode(otpAuthUri, QRCodeGenerator.ECCLevel.M);
        var qrCode = new PngByteQRCode(qrCodeData);
        var qrCodeImage = qrCode.GetGraphic(5); // 5 pixels per module

        _logger.LogInformation("TOTP setup initiated for user {UserId}", userId);

        return new TotpSetupResponse
        {
            QrCodeImage = qrCodeImage,
            ManualEntryKey = base32Secret,
            OtpAuthUri = otpAuthUri
        };
    }

    /// <summary>
    /// Step 2 of TOTP setup: verifies the code from the authenticator app.
    /// If the code matches, 2FA is enabled with TOTP method.
    /// Uses ±1 step window (30 seconds before/after) for clock skew tolerance.
    /// </summary>
    public async Task<bool> VerifyTotpSetupAsync(long userId, string code, CancellationToken ct = default)
    {
        var user = await _context.User.FindAsync([userId], ct)
            ?? throw new InvalidOperationException($"User with ID {userId} not found.");

        // User must have initiated setup first (secret must exist)
        if (string.IsNullOrEmpty(user.TotpSecretEncrypted))
        {
            _logger.LogWarning("TOTP verify attempted without setup for user {UserId}", userId);
            return false;
        }

        // Decrypt the stored secret and verify the code
        var base32Secret = _protector.Unprotect(user.TotpSecretEncrypted);
        if (!VerifyTotpCode(base32Secret, code))
        {
            _logger.LogWarning("Invalid TOTP code during setup for user {UserId}", userId);
            return false;
        }

        // Code is valid — enable 2FA with TOTP method
        user.TwoFactorEnabled = true;
        user.TwoFactorMethod = ETwoFactorMethod.Totp;
        user.TwoFactorEnabledAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("TOTP 2FA enabled for user {UserId}", userId);
        return true;
    }

    // ─── Email 2FA Setup ─────────────────────────────────────────────────────

    /// <summary>
    /// Enables email-based 2FA. No verification step needed — just sets the method.
    /// Requires a verified email address (otherwise there's no way to send codes).
    /// </summary>
    public async Task EnableEmailTwoFactorAsync(long userId, CancellationToken ct = default)
    {
        var user = await _context.User.FindAsync([userId], ct)
            ?? throw new InvalidOperationException($"User with ID {userId} not found.");

        // Email must be verified — otherwise we can't send OTP codes
        if (!user.IsEmailVerified)
        {
            throw new InvalidOperationException(
                "Cannot enable email-based 2FA: email address is not verified.");
        }

        user.TwoFactorEnabled = true;
        user.TwoFactorMethod = ETwoFactorMethod.Email;
        user.TwoFactorEnabledAt = DateTime.UtcNow;
        // Clear any TOTP secret if switching from TOTP to Email
        user.TotpSecretEncrypted = null;
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Email 2FA enabled for user {UserId}", userId);
    }

    // ─── Login Flow (Step 2) ─────────────────────────────────────────────────

    /// <summary>
    /// Called by AuthService after successful password verification when 2FA is enabled.
    /// Generates an encrypted session token and (for email method) sends the OTP code.
    ///
    /// The session token format is "userId|timestamp" encrypted with Data Protection.
    /// This ensures:
    /// 1. The token can only be decrypted by this server
    /// 2. The userId is bound to the token (prevents token reuse for other users)
    /// 3. The timestamp allows server-side expiration checks
    /// </summary>
    public async Task<string> GenerateTwoFactorSessionTokenAsync(long userId, CancellationToken ct = default)
    {
        var user = await _context.User.FindAsync([userId], ct)
            ?? throw new InvalidOperationException($"User with ID {userId} not found.");

        // Create a session token protector (different purpose from TOTP secret)
        var sessionProtector = _protector.CreateProtector(SessionTokenPurpose);
        var tokenPayload = $"{userId}|{DateTime.UtcNow:O}";
        var encryptedToken = sessionProtector.Protect(tokenPayload);

        // Store the token and its expiration on the user record
        user.TwoFactorSessionToken = encryptedToken;
        user.TwoFactorSessionTokenExpiresAt = DateTime.UtcNow.AddMinutes(SessionTokenExpirationMinutes);
        user.FailedTwoFactorAttempts = 0; // Reset attempt counter for new session

        // For email method: generate and send a 6-digit OTP code
        if (user.TwoFactorMethod == ETwoFactorMethod.Email)
        {
            var code = GenerateRandomCode(EmailCodeLength);
            // BCrypt hash the code before storing — same security as passwords
            user.TwoFactorEmailCode = BCrypt.Net.BCrypt.HashPassword(code, workFactor: 12);
            user.TwoFactorEmailCodeExpiresAt = DateTime.UtcNow.AddMinutes(EmailCodeExpirationMinutes);

            await _context.SaveChangesAsync(ct);

            // Send the OTP code via email using the content template system
            await SendTwoFactorEmailAsync(user, code, ct);
        }
        else
        {
            await _context.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "2FA session token generated for user {UserId} (method: {Method})",
            userId, user.TwoFactorMethod);

        return encryptedToken;
    }

    /// <summary>
    /// Verifies the 2FA code during login (step 2 of the two-step login flow).
    ///
    /// Validation steps:
    /// 1. Decrypt and validate the session token
    /// 2. Find the user by the token
    /// 3. Check session expiration
    /// 4. Check rate limiting (max 5 failed attempts)
    /// 5. Verify the code (TOTP or email OTP)
    /// 6. On success: clear session data and return userId
    /// 7. On failure: increment attempt counter
    /// </summary>
    public async Task<long?> VerifyTwoFactorCodeAsync(string sessionToken, string code, CancellationToken ct = default)
    {
        // Find the user who has this exact session token stored
        var user = await _context.User
            .FirstOrDefaultAsync(u => u.TwoFactorSessionToken == sessionToken, ct);

        if (user == null)
        {
            _logger.LogWarning("2FA verification attempted with unknown session token");
            return null;
        }

        // Check if the session token has expired (5-minute window)
        if (user.TwoFactorSessionTokenExpiresAt.HasValue &&
            user.TwoFactorSessionTokenExpiresAt.Value < DateTime.UtcNow)
        {
            _logger.LogWarning("2FA session expired for user {UserId}", user.Id);
            ClearSessionData(user);
            await _context.SaveChangesAsync(ct);
            return null;
        }

        // Rate limiting: check if the user has exceeded max failed attempts
        if (user.FailedTwoFactorAttempts >= MaxFailedAttempts)
        {
            _logger.LogWarning(
                "2FA session invalidated — too many failed attempts for user {UserId}",
                user.Id);
            ClearSessionData(user);
            await _context.SaveChangesAsync(ct);
            return null;
        }

        // Verify the code based on the user's 2FA method
        bool isValid;
        if (user.TwoFactorMethod == ETwoFactorMethod.Totp)
        {
            // TOTP: decrypt the secret and verify the time-based code
            if (string.IsNullOrEmpty(user.TotpSecretEncrypted))
            {
                _logger.LogError("TOTP secret missing for user {UserId} during 2FA verification", user.Id);
                return null;
            }

            var base32Secret = _protector.Unprotect(user.TotpSecretEncrypted);
            isValid = VerifyTotpCode(base32Secret, code);
        }
        else if (user.TwoFactorMethod == ETwoFactorMethod.Email)
        {
            // Email: verify the BCrypt-hashed OTP code and check its expiration
            isValid = !string.IsNullOrEmpty(user.TwoFactorEmailCode)
                && user.TwoFactorEmailCodeExpiresAt.HasValue
                && user.TwoFactorEmailCodeExpiresAt.Value >= DateTime.UtcNow
                && BCrypt.Net.BCrypt.Verify(code, user.TwoFactorEmailCode);
        }
        else
        {
            _logger.LogError("Unknown 2FA method for user {UserId}: {Method}", user.Id, user.TwoFactorMethod);
            return null;
        }

        if (!isValid)
        {
            // Increment failed attempt counter
            user.FailedTwoFactorAttempts++;
            await _context.SaveChangesAsync(ct);

            _logger.LogWarning(
                "Invalid 2FA code for user {UserId} (attempt {Attempt}/{Max})",
                user.Id, user.FailedTwoFactorAttempts, MaxFailedAttempts);
            return null;
        }

        // Code is valid — clear session data and return the user ID for JWT generation
        ClearSessionData(user);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("2FA verification successful for user {UserId}", user.Id);
        return user.Id;
    }

    // ─── Disable 2FA ─────────────────────────────────────────────────────────

    /// <summary>
    /// Disables 2FA for a user (self-service). Clears all 2FA-related fields.
    /// Password verification is done at the controller level before calling this.
    /// </summary>
    public async Task DisableTwoFactorAsync(long userId, CancellationToken ct = default)
    {
        var user = await _context.User.FindAsync([userId], ct)
            ?? throw new InvalidOperationException($"User with ID {userId} not found.");

        ClearAllTwoFactorData(user);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("2FA disabled by user {UserId}", userId);
    }

    /// <summary>
    /// Force-disables 2FA for a user (admin action). Used when a user is locked out
    /// and cannot provide a 2FA code. No password verification required.
    /// </summary>
    public async Task ForceDisableTwoFactorAsync(long userId, long adminUserId, CancellationToken ct = default)
    {
        var user = await _context.User.FindAsync([userId], ct)
            ?? throw new InvalidOperationException($"User with ID {userId} not found.");

        ClearAllTwoFactorData(user);
        await _context.SaveChangesAsync(ct);

        _logger.LogWarning(
            "2FA force-disabled for user {UserId} by admin {AdminUserId}",
            userId, adminUserId);
    }

    // ─── Status ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current 2FA status for a user.
    /// </summary>
    public async Task<TwoFactorStatusDto> GetTwoFactorStatusAsync(long userId, CancellationToken ct = default)
    {
        var user = await _context.User
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new InvalidOperationException($"User with ID {userId} not found.");

        return new TwoFactorStatusDto
        {
            Enabled = user.TwoFactorEnabled,
            Method = user.TwoFactorMethod,
            EnabledAt = user.TwoFactorEnabledAt
        };
    }

    // ─── Private Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Verifies a TOTP code against a base32-encoded secret.
    /// Uses OtpNet library with ±1 step tolerance (30 seconds before/after current window).
    /// This compensates for slight clock differences between server and user's device.
    /// </summary>
    private static bool VerifyTotpCode(string base32Secret, string code)
    {
        var secretBytes = Base32Encoding.ToBytes(base32Secret);
        var totp = new Totp(secretBytes, step: 30, mode: OtpHashMode.Sha1, totpSize: 6);
        // VerificationWindow of 1 allows ±1 step (code from 30s ago or 30s ahead)
        return totp.VerifyTotp(code, out _, new VerificationWindow(previous: 1, future: 1));
    }

    /// <summary>
    /// Generates a cryptographically secure random numeric code of the specified length.
    /// Uses RandomNumberGenerator (CSPRNG) instead of Random for security.
    /// </summary>
    private static string GenerateRandomCode(int length)
    {
        // Generate a random number between 0 and 10^length - 1, then pad with leading zeros
        var max = (int)Math.Pow(10, length);
        var randomNumber = RandomNumberGenerator.GetInt32(max);
        return randomNumber.ToString($"D{length}");
    }

    /// <summary>
    /// Sends a 2FA email with the 6-digit OTP code using the content template system.
    /// Falls back to a simple inline template if no TwoFactorEmail template is configured.
    /// </summary>
    private async Task SendTwoFactorEmailAsync(
        Domain.Entities.User user, string code, CancellationToken ct)
    {
        var appName = _configuration["AppSettings:Name"] ?? "InvoiceApi";
        var placeholders = new Dictionary<string, string>
        {
            ["FullName"] = user.FullName,
            ["Code"] = code,
            ["ExpirationMinutes"] = EmailCodeExpirationMinutes.ToString(),
            ["AppName"] = appName
        };

        try
        {
            // Try to use the content template system (TwoFactorEmail = 23)
            var template = await _contentTemplateService
                .GetDefaultByTypeAsync(EContentTemplateType.TwoFactorEmail, ct);

            if (template != null)
            {
                // Render the template with placeholders
                var (subject, htmlBody) = await _contentTemplateService
                    .RenderTemplateAsync(template.Id, placeholders, ct);

                await _emailService.SendEmailAsync(
                    user.Email,
                    subject ?? $"Your verification code — {appName}",
                    htmlBody,
                    ct: ct);
            }
            else
            {
                // Fallback: no template configured, use a simple inline email
                var subject = $"Your verification code — {appName}";
                var htmlBody = $@"
                    <div style=""font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;"">
                        <h2 style=""color: #1976D2;"">Verification Code</h2>
                        <p>Hello <strong>{user.FullName}</strong>,</p>
                        <p>Your two-factor authentication code is:</p>
                        <div style=""text-align: center; margin: 30px 0;"">
                            <span style=""background-color: #f5f5f5; padding: 16px 32px; font-size: 32px;
                                         font-weight: bold; letter-spacing: 8px; border-radius: 8px;
                                         border: 2px solid #1976D2;"">{code}</span>
                        </div>
                        <p style=""color: #666; font-size: 14px;"">
                            This code is valid for <strong>{EmailCodeExpirationMinutes} minutes</strong>.
                        </p>
                        <hr style=""border: none; border-top: 1px solid #eee; margin: 20px 0;"" />
                        <p style=""color: #999; font-size: 12px;"">{appName}</p>
                    </div>";

                await _emailService.SendEmailAsync(user.Email, subject, htmlBody, ct: ct);
            }

            _logger.LogInformation("2FA email code sent to user {UserId}", user.Id);
        }
        catch (Exception ex)
        {
            // Email failure should not block the login flow — the user can retry
            _logger.LogError(ex, "Failed to send 2FA email to user {UserId}", user.Id);
            throw new InvalidOperationException(
                "Failed to send verification code email. Please try again.", ex);
        }
    }

    /// <summary>
    /// Clears temporary session data (token, email code, attempt counter).
    /// Called after successful verification or when the session is invalidated.
    /// </summary>
    private static void ClearSessionData(Domain.Entities.User user)
    {
        user.TwoFactorSessionToken = null;
        user.TwoFactorSessionTokenExpiresAt = null;
        user.TwoFactorEmailCode = null;
        user.TwoFactorEmailCodeExpiresAt = null;
        user.FailedTwoFactorAttempts = 0;
    }

    /// <summary>
    /// Clears ALL 2FA-related fields — used when disabling 2FA entirely.
    /// Removes the method, secret, codes, session data, and timestamps.
    /// </summary>
    private static void ClearAllTwoFactorData(Domain.Entities.User user)
    {
        user.TwoFactorEnabled = false;
        user.TwoFactorMethod = ETwoFactorMethod.None;
        user.TotpSecretEncrypted = null;
        user.TwoFactorEnabledAt = null;
        ClearSessionData(user);
    }
}
