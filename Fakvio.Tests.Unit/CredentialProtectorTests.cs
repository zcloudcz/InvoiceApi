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

    /// <summary>
    /// Simulates the key-ring rotation bug: a value encrypted with one DataProtection key ring
    /// cannot be decrypted by a DIFFERENT key ring (different app name / ephemeral keys).
    ///
    /// This is exactly what happens in production when Data Protection keys are NOT persisted
    /// to the database: after a restart the app generates a new key ring and can no longer
    /// decrypt the IMAP password stored by the previous instance.
    ///
    /// The "recovery" behaviour — returning the raw ciphertext — must NOT silently succeed:
    /// the test verifies that Decrypt() returns the ciphertext unchanged (not the plaintext),
    /// so callers are aware they received garbage and the bug is detectable in logs.
    /// </summary>
    [Fact]
    public void Decrypt_WithDifferentKeyRing_ReturnsRawCiphertext_NotPlaintext()
    {
        // Arrange — encrypt with instance1 (simulates first app start)
        var instance1Provider = DataProtectionProvider.Create("Fakvio");
        var instance1Logger = Substitute.For<ILogger<CredentialProtector>>();
        var instance1 = new CredentialProtector(instance1Provider, instance1Logger);

        const string plaintext = "secret-imap-password";
        var ciphertext = instance1.Encrypt(plaintext)!;

        // Act — try to decrypt with a DIFFERENT key ring (simulates restart without key persistence)
        var instance2Provider = DataProtectionProvider.Create("AnotherApp"); // different purpose → different keys
        var instance2Logger = Substitute.For<ILogger<CredentialProtector>>();
        var instance2 = new CredentialProtector(instance2Provider, instance2Logger);

        var result = instance2.Decrypt(ciphertext);

        // Assert — must NOT be the original plaintext
        // (CredentialProtector falls back to returning the raw ciphertext after CryptographicException)
        result.ShouldNotBe(plaintext,
            "Decrypt with a different key ring must not accidentally return the original password. " +
            "This test confirms that the key-ring isolation works correctly.");

        // The returned value is the original ciphertext (legacy-plaintext fallback path)
        result.ShouldBe(ciphertext,
            "Migration-safety fallback returns the raw ciphertext unchanged when decryption fails.");
    }

    /// <summary>
    /// Verifies that two CredentialProtector instances sharing the same ApplicationName
    /// CAN decrypt each other's values — as they will after PersistKeysToDbContext is added.
    ///
    /// When Data Protection keys are persisted to the database (PersistKeysToDbContext<MasterDbContext>),
    /// all application instances (API + Functions, multi-replica) share the same key ring.
    /// This test confirms that matching application names is the necessary prerequisite.
    /// </summary>
    [Fact]
    public void Decrypt_SameApplicationName_CanDecryptAcrossInstances()
    {
        // Arrange — two separate instances with the SAME application name (shared key ring path)
        var provider1 = DataProtectionProvider.Create("Fakvio");
        var provider2 = DataProtectionProvider.Create("Fakvio");
        var logger = Substitute.For<ILogger<CredentialProtector>>();

        var instance1 = new CredentialProtector(provider1, logger);
        var instance2 = new CredentialProtector(provider2, logger);

        const string plaintext = "shared-secret";
        var ciphertext = instance1.Encrypt(plaintext)!;

        // Act
        var result = instance2.Decrypt(ciphertext);

        // Assert — same app name → same key derivation → cross-instance decrypt succeeds
        result.ShouldBe(plaintext,
            "Instances sharing the same ApplicationName must be able to decrypt each other's values. " +
            "This mirrors the production setup where PersistKeysToDbContext shares one key ring.");
    }
}
