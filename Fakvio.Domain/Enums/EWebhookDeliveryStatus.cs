namespace Fakvio.Domain.Enums;

/// <summary>
/// Lifecycle status of a single outbound webhook delivery attempt chain (DEVGUIDE §4.x Webhooks).
/// One <c>WebhookDelivery</c> row represents one business event sent to one subscription — it is
/// retried with backoff (see <c>WebhookDispatchService</c>) and ends in either Succeeded or Failed.
/// </summary>
public enum EWebhookDeliveryStatus
{
    /// <summary>Waiting for its next attempt (NextAttemptAt in the future or due now).</summary>
    Pending = 0,

    /// <summary>Delivered successfully — the endpoint returned a 2xx status code.</summary>
    Succeeded = 1,

    /// <summary>All retry attempts exhausted without a 2xx response — terminal, not retried again.</summary>
    Failed = 2,
}
