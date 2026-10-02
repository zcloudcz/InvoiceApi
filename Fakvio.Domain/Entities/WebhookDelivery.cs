using Fakvio.Domain.Common;
using Fakvio.Domain.Enums;

namespace Fakvio.Domain.Entities;

/// <summary>
/// One outbound delivery attempt chain for a single business event sent to a single
/// <see cref="WebhookSubscription"/> — the transactional outbox row for webhooks
/// (DEVGUIDE §4.x). Created by <c>IWebhookPublisher</c> in the same DbContext/SaveChanges
/// as the business change where feasible, then picked up and retried by
/// <c>IWebhookDispatchService.RunCycleAsync</c> until it succeeds or exhausts retries.
///
/// Retention: rows older than 30 days (by <see cref="BaseEntity.CreatedAt"/>) in a terminal
/// status (Succeeded/Failed) are deleted by the dispatcher cycle — this is an audit/debug log,
/// not permanent storage.
/// </summary>
public class WebhookDelivery : BaseEntity
{
    /// <summary>FK to the subscription this delivery is addressed to.</summary>
    public long SubscriptionId { get; set; }

    /// <summary>Navigation to the subscription (URL, secret, active flag).</summary>
    public WebhookSubscription Subscription { get; set; } = null!;

    /// <summary>
    /// Stable event id sent as the <c>Fakvio-Webhook-Id</c> header and in the payload's
    /// <c>id</c> field. Shared by every subscription notified for the same business event,
    /// so the receiver can deduplicate retried/re-sent deliveries.
    /// </summary>
    public Guid EventId { get; set; }

    /// <summary>Event name, e.g. "invoice.paid" — denormalized from the subscription match for easy filtering in the deliveries log.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>The exact JSON body sent (and re-sent on retry/redeliver) to the endpoint.</summary>
    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>Current delivery status.</summary>
    public EWebhookDeliveryStatus Status { get; set; } = EWebhookDeliveryStatus.Pending;

    /// <summary>Number of delivery attempts made so far (0 = not attempted yet).</summary>
    public int Attempts { get; set; }

    /// <summary>When the dispatcher should try next. Set to "now" on enqueue, advanced by backoff on failure.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>HTTP status code of the most recent attempt, if one was made.</summary>
    public int? LastStatusCode { get; set; }

    /// <summary>Error from the most recent attempt (timeout, connection refused, SSRF rejection, non-2xx body excerpt), truncated.</summary>
    public string? LastError { get; set; }

    /// <summary>When the delivery succeeded (2xx response). Null while pending or failed.</summary>
    public DateTimeOffset? DeliveredAt { get; set; }
}
