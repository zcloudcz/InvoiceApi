using Fakvio.Domain.Common;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Per-tenant outbound webhook subscription — a URL the tenant wants notified on business
/// events (invoice paid, received invoice created, ...). See DEVGUIDE §4.15 Webhooks.
///
/// Security:
///   - <see cref="SecretEncrypted"/> is encrypted at rest with <c>ICredentialProtector</c>,
///     the same Data Protection mechanism used for SMTP/IMAP passwords (DEVGUIDE §2.6).
///     The plaintext secret is shown to the user once, at creation and at each rotation —
///     it is never returned by a subsequent read.
///   - <see cref="Url"/> is validated at write time and re-validated at connect time
///     (SSRF guard, see <c>WebhookUrlGuard</c>) — a tenant could otherwise point a webhook
///     at an internal/cloud-metadata address and use the dispatcher as an SSRF proxy.
/// </summary>
public class WebhookSubscription : BaseEntity
{
    /// <summary>Destination URL — https only in production (http://localhost allowed in Development).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Free-form label so the user can tell subscriptions apart ("Shop order sync").</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Event names this subscription wants (e.g. "invoice.paid"). Stored as a single
    /// comma-joined string via a value converter (<c>TenantDbContext</c>) — simplest mapping
    /// that works with plain PostgreSQL text, no array/JSON column needed for a short list.
    /// </summary>
    public List<string> Events { get; set; } = new();

    /// <summary>Encrypted HMAC signing secret (32 random bytes, base64, then Data-Protection-encrypted).</summary>
    public string SecretEncrypted { get; set; } = string.Empty;

    /// <summary>Whether this subscription currently receives events. Paused subscriptions are skipped by the publisher.</summary>
    public bool IsActive { get; set; } = true;
}
