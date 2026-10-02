using Fakvio.Contracts.Dto.Webhook;

namespace Fakvio.Application.Service;

/// <summary>CRUD + management operations for webhook subscriptions, backing the <c>api/webhooks</c> controller.</summary>
public interface IWebhookSubscriptionService
{
    Task<List<WebhookSubscriptionDto>> GetAllAsync(CancellationToken ct = default);

    Task<WebhookSubscriptionDto?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>Creates a subscription with a freshly generated secret, returned once in plaintext.</summary>
    Task<WebhookSubscriptionCreatedDto> CreateAsync(CreateWebhookSubscriptionDto dto, CancellationToken ct = default);

    Task<WebhookSubscriptionDto> UpdateAsync(long id, UpdateWebhookSubscriptionDto dto, CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>Generates a new secret for the subscription, invalidating the old one. Returned once in plaintext.</summary>
    Task<WebhookSubscriptionCreatedDto> RotateSecretAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Sends a "ping" test event to the subscription's URL synchronously (not queued) and
    /// returns whether it succeeded. Used by the UI's "Test" button for immediate feedback.
    /// </summary>
    Task<WebhookTestResultDto> TestAsync(long id, CancellationToken ct = default);

    Task<List<WebhookDeliveryDto>> GetDeliveriesAsync(long subscriptionId, CancellationToken ct = default);

    /// <summary>Resets a delivery to Pending/Attempts=0 so the dispatcher retries it on its next cycle.</summary>
    Task RedeliverAsync(long deliveryId, CancellationToken ct = default);
}
