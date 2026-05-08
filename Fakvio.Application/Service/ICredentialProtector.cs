namespace Fakvio.Application.Service;

/// <summary>
/// Service for encrypting and decrypting sensitive credentials stored in the database.
///
/// Uses ASP.NET Core Data Protection API under the hood — the same mechanism that protects
/// cookies, anti-forgery tokens, and TwoFactorService TOTP secrets in this application.
///
/// Usage pattern:
///   - Call Encrypt() before saving a credential to the database.
///   - Call Decrypt() after reading a credential from the database.
///   - Both methods are null-safe: null/empty input returns null/empty output.
///   - Decrypt() is migration-safe: if the value is not encrypted (legacy plaintext),
///     it returns the original value instead of throwing. Next save will encrypt it.
///
/// Encrypted fields in CompanySystemSettings:
///   SmtpPassword, AiClaudeApiKey, AiOpenAiApiKey, AiGeminiApiKey,
///   GoogleDriveAccessToken, GoogleDriveRefreshToken,
///   OneDriveAccessToken, OneDriveRefreshToken
///
/// Encrypted fields in SystemConfiguration:
///   SmtpPassword
///
/// Junior note: We encrypt credentials at rest so that if someone gets access to the database
/// (SQL injection, backup leak, insider threat), they cannot read the actual passwords/keys.
/// The encryption key is stored on the application server, NOT in the database.
/// </summary>
public interface ICredentialProtector
{
    /// <summary>
    /// Encrypts a plaintext credential for safe database storage.
    /// Returns null if the input is null or empty (nothing to encrypt).
    /// </summary>
    /// <param name="plaintext">The plaintext credential (password, API key, token).</param>
    /// <returns>Encrypted string, or null/empty if input was null/empty.</returns>
    string? Encrypt(string? plaintext);

    /// <summary>
    /// Decrypts an encrypted credential read from the database.
    /// Returns null if the input is null or empty.
    /// Migration-safe: if the value is not encrypted (legacy plaintext from before
    /// encryption was implemented), returns the original value without throwing.
    /// </summary>
    /// <param name="ciphertext">The encrypted credential from the database.</param>
    /// <returns>Decrypted plaintext credential, or original value if not encrypted.</returns>
    string? Decrypt(string? ciphertext);

    /// <summary>
    /// Checks whether a stored encrypted credential can be successfully decrypted
    /// with the current Data Protection key ring.
    ///
    /// Returns true when the value is:
    ///   - null or empty (field not configured — healthy by definition)
    ///   - successfully decrypted AND the decrypted result does NOT start with "CfDJ8"
    ///     (the base64 prefix of a Data Protection ciphertext payload)
    ///
    /// Returns false when:
    ///   - Decryption fails (wrong/lost key ring) — Decrypt() falls back to returning
    ///     the raw ciphertext unchanged, which still starts with "CfDJ8".
    ///
    /// Junior note: "CfDJ8" is always the start of an ASP.NET Core Data Protection
    /// ciphertext when base64-encoded. If we decrypt and still see this prefix, the
    /// decryption silently failed and we got back the original garbage.
    /// </summary>
    /// <param name="encryptedValue">Encrypted credential value from the database.</param>
    /// <returns>True if healthy (null, empty, or correctly decryptable); false if corrupt.</returns>
    bool IsHealthy(string? encryptedValue);
}
