using System.Security.Cryptography;
using Fakvio.Application.Service;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Encrypts and decrypts sensitive credentials using ASP.NET Core Data Protection API.
///
/// The "purpose" string ("Fakvio.Credentials.v1") creates a unique encryption scope.
/// Different purpose strings produce different encryption keys from the same master key,
/// so TOTP secrets (TwoFactorService) and SMTP passwords (this service) cannot be
/// cross-decrypted even if both use the same IDataProtectionProvider.
///
/// Migration safety: Decrypt() catches CryptographicException when attempting to unprotect
/// a value that was stored as plaintext before encryption was implemented. Instead of throwing,
/// it returns the original plaintext value. The next time the value is saved, Encrypt() will
/// encrypt it properly — making migration automatic and zero-downtime.
///
/// Junior note: Data Protection API uses AES-256-CBC + HMACSHA256 by default. The encryption
/// keys are stored on the server (in the key ring directory, Azure Key Vault, or Azure Blob Storage
/// depending on the hosting environment). They rotate automatically every 90 days,
/// and the API handles key rotation transparently.
/// </summary>
public class CredentialProtector : ICredentialProtector
{
    private readonly IDataProtector _protector;
    private readonly ILogger<CredentialProtector> _logger;

    /// <summary>
    /// Purpose string that scopes the encryption to credential protection.
    /// Changing this would make all previously encrypted values unreadable,
    /// so treat it as immutable once deployed to production.
    /// </summary>
    private const string Purpose = "Fakvio.Credentials.v1";

    public CredentialProtector(
        IDataProtectionProvider dataProtectionProvider,
        ILogger<CredentialProtector> logger)
    {
        // CreateProtector with a purpose string creates a derived encryption key.
        // The same purpose always produces the same key (deterministic).
        _protector = dataProtectionProvider.CreateProtector(Purpose);
        _logger = logger;
    }

    /// <inheritdoc />
    public string? Encrypt(string? plaintext)
    {
        // Null/empty credentials should remain null/empty — no point encrypting nothing.
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;

        return _protector.Protect(plaintext);
    }

    /// <inheritdoc />
    public string? Decrypt(string? ciphertext)
    {
        // Null/empty means "not configured" — pass through unchanged.
        if (string.IsNullOrEmpty(ciphertext))
            return ciphertext;

        try
        {
            return _protector.Unprotect(ciphertext);
        }
        catch (CryptographicException)
        {
            // The value is not encrypted — it's legacy plaintext from before encryption
            // was implemented. Return the original value. The next time the user saves
            // their settings, Encrypt() will encrypt it automatically.
            // This makes the migration from plaintext → encrypted zero-downtime.
            _logger.LogWarning(
                "Credential value could not be decrypted — treating as legacy plaintext. " +
                "It will be encrypted automatically on next save.");
            return ciphertext;
        }
    }
}
