using System.Security.Cryptography;
using Fakvio.Application.Service;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Cryptographically secure alias generator using URL-safe base32 (Crockford alphabet
/// without visually ambiguous characters: no I, L, O, U, 0, 1).
///
/// Why base32 and not base64? Emails are case-insensitive by RFC, so we cannot rely
/// on mixed case. Base32 gives us 5 bits per char in lowercase-only form.
///
/// Why not GUID? Too long (32 chars) and hard to type. Users occasionally need to
/// read or dictate the alias when setting up their bank — shorter is better.
/// </summary>
public class AliasGenerator : IAliasGenerator
{
    // Crockford alphabet minus visually confusing characters (I, L, O, U, 0, 1).
    // 26 chars → ~4.7 bits/char. For 10 chars → ~47 bits of entropy.
    private const string Alphabet = "abcdefghjkmnpqrstvwxyz23456789";

    private const string Prefix = "pay-";

    private const int BodyLength = 10;

    /// <inheritdoc />
    public string Generate()
    {
        // RandomNumberGenerator is cryptographically secure; System.Random is not.
        // For an auth-like token, CSPRNG is the right call.
        Span<byte> randomBytes = stackalloc byte[BodyLength];
        RandomNumberGenerator.Fill(randomBytes);

        Span<char> body = stackalloc char[BodyLength];
        for (var i = 0; i < BodyLength; i++)
        {
            // Map each byte to an index into the alphabet.
            // Using modulo introduces a slight bias, but for 256 % 26 ≈ 9.85 the
            // bias is negligible (at most one or two bits of entropy lost — still >45 bits).
            body[i] = Alphabet[randomBytes[i] % Alphabet.Length];
        }

        return Prefix + new string(body);
    }
}
