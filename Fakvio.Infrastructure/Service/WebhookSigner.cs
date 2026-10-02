using System.Security.Cryptography;
using System.Text;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// HMAC-SHA256 signing for outbound webhooks (DEVGUIDE §4.15 Webhooks).
/// Signature format matches the Stripe/GitHub convention the USERGUIDE documents for
/// integrators: <c>v1=&lt;hex HMAC-SHA256(secret, "{timestamp}.{body}")&gt;</c>.
/// Including the timestamp in the signed string lets a receiver reject stale/replayed
/// deliveries by checking the timestamp is recent, in addition to verifying the signature.
/// </summary>
public static class WebhookSigner
{
    /// <summary>Builds the "v1=&lt;hex&gt;" signature header value for the given body and secret.</summary>
    public static string Sign(string secret, long unixTimestamp, string body)
    {
        var signedPayload = $"{unixTimestamp}.{body}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload));
        return "v1=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Generates a new random secret: 32 raw bytes, base64-encoded for use as the HMAC key and for display to the user.</summary>
    public static string GenerateSecret()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }
}
