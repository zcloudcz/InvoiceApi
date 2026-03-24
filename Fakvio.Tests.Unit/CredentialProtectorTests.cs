using Fakvio.Infrastructure.Service;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for CredentialProtector — the service that encrypts/decrypts sensitive credentials
/// (SMTP passwords, AI API keys, OAuth tokens) stored in the database.
///
/// Uses an ephemeral DataProtectionProvider (in-memory keys) — no persistent key storage needed.
/// This mirrors how TwoFactorServiceTests tests Data Protection API usage.
///
/// Key behaviors tested:
/// 1. Encrypt → Decrypt roundtrip returns original plaintext
/// 2. Null/empty input passes through unchanged
/// 3. Legacy plaintext values (not encrypted) are returned unchanged by Decrypt (migration safety)
/// 4. Encrypted output is different from plaintext (actually encrypted, not a no-op)
/// </summary>
public class CredentialProtectorTests
{
    private readonly CredentialProtector _protector;

    public CredentialProtectorTests()
    {
        // Ephemeral Data Protection provider — keys exist only in memory for testing.
        // "CredentialProtectorTests" is just a label, not the encryption purpose.
        var dataProtectionProvider = DataProtectionProvider.Create("CredentialProtectorTests");
        var logger = Substitute.For<ILogger<CredentialProtector>>();

        _protector = new CredentialProtector(dataProtectionProvider, logger);
    }

    /// <summary>
    /// Encrypt then Decrypt must return the original plaintext — the fundamental roundtrip guarantee.
    /// </summary>
    [Theory]
    [InlineData("simple-password")]
    [InlineData("p@$$w0rd!#%^&*()")] // Special characters
    [InlineData("sk-ant-api03-xxxxxxxxxxxx")] // API key format
    [InlineData("ěščřžýáíé")] // Czech diacritics (UTF-8)
    [InlineData("a")] // Single character
    [InlineData("a very long password that exceeds typical field lengths and contains unicode: 你好世界 🔑")]
    public void Encrypt_Decrypt_Roundtrip_ReturnsOriginalValue(string plaintext)
    {
        // Act
        var encrypted = _protector.Encrypt(plaintext);
        var decrypted = _protector.Decrypt(encrypted);

        // Assert
        decrypted.ShouldBe(plaintext);
    }

    /// <summary>
    /// Encrypted output must NOT equal the plaintext — otherwise it's not actually encrypting.
    /// </summary>
    [Fact]
    public void Encrypt_ProducesDifferentOutput()
    {
        var plaintext = "my-secret-password";

        var encrypted = _protector.Encrypt(plaintext);

        encrypted.ShouldNotBe(plaintext);
        encrypted.ShouldNotBeNullOrEmpty();
    }

    /// <summary>
    /// Null input should pass through unchanged — null means "not configured".
    /// </summary>
    [Fact]
    public void Encrypt_NullInput_ReturnsNull()
    {
        _protector.Encrypt(null).ShouldBeNull();
    }

    /// <summary>
    /// Empty string should pass through unchanged — empty means "cleared".
    /// </summary>
    [Fact]
    public void Encrypt_EmptyInput_ReturnsEmpty()
    {
        _protector.Encrypt("").ShouldBe("");
    }

    [Fact]
    public void Decrypt_NullInput_ReturnsNull()
    {
        _protector.Decrypt(null).ShouldBeNull();
    }

    [Fact]
    public void Decrypt_EmptyInput_ReturnsEmpty()
    {
        _protector.Decrypt("").ShouldBe("");
    }

    /// <summary>
    /// Migration safety: when Decrypt receives a value that was NOT encrypted (legacy plaintext
    /// from before encryption was implemented), it should return the original value unchanged
    /// instead of throwing CryptographicException.
    /// </summary>
    [Theory]
    [InlineData("legacy-plain-password")]
    [InlineData("sk-ant-api03-old-key")]
    [InlineData("smtp-password-from-before-encryption")]
    public void Decrypt_LegacyPlaintext_ReturnsSameValue(string legacyPlaintext)
    {
        // Act — this is NOT an encrypted value, just a raw string from the DB
        var result = _protector.Decrypt(legacyPlaintext);

        // Assert — should return the plaintext unchanged (migration-safe)
        result.ShouldBe(legacyPlaintext);
    }

    /// <summary>
    /// Two encryptions of the same plaintext should produce different ciphertext
    /// (Data Protection API uses random IVs, so each encryption is unique).
    /// This ensures we're not using a deterministic cipher mode.
    /// </summary>
    [Fact]
    public void Encrypt_SamePlaintext_ProducesDifferentCiphertexts()
    {
        var plaintext = "same-password";

        var encrypted1 = _protector.Encrypt(plaintext);
        var encrypted2 = _protector.Encrypt(plaintext);

        // Both should decrypt to the same value
        _protector.Decrypt(encrypted1).ShouldBe(plaintext);
        _protector.Decrypt(encrypted2).ShouldBe(plaintext);

        // But the encrypted values should be different (random IV)
        encrypted1.ShouldNotBe(encrypted2);
    }
}
