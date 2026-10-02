using Fakvio.Contracts.Dto.Webhook;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>API client for outbound webhook subscriptions (WebhookController, DEVGUIDE §4.15).</summary>
public class WebhookApiService : ApiClientBase
{
    public WebhookApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<WebhookApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    public async Task<List<WebhookSubscriptionDto>> GetAllAsync()
        => await GetAsync<List<WebhookSubscriptionDto>>("/api/webhooks") ?? [];

    /// <summary>Creates a subscription; the result carries the plaintext secret (shown once).</summary>
    public Task<WebhookSubscriptionCreatedDto?> CreateAsync(CreateWebhookSubscriptionDto dto)
        => PostAsync<CreateWebhookSubscriptionDto, WebhookSubscriptionCreatedDto>("/api/webhooks", dto);

    public Task<WebhookSubscriptionDto?> UpdateAsync(long id, UpdateWebhookSubscriptionDto dto)
        => PutAsync<UpdateWebhookSubscriptionDto, WebhookSubscriptionDto>($"/api/webhooks/{id}", dto);

    public Task<bool> DeleteAsync(long id) => base.DeleteAsync($"/api/webhooks/{id}");

    public Task<WebhookSubscriptionCreatedDto?> RotateSecretAsync(long id)
        => PostWithoutBodyAsync<WebhookSubscriptionCreatedDto>($"/api/webhooks/{id}/rotate-secret");

    public Task<WebhookTestResultDto?> TestAsync(long id)
        => PostWithoutBodyAsync<WebhookTestResultDto>($"/api/webhooks/{id}/test");

    public async Task<List<WebhookDeliveryDto>> GetDeliveriesAsync(long id)
        => await GetAsync<List<WebhookDeliveryDto>>($"/api/webhooks/{id}/deliveries") ?? [];

    public Task<bool> RedeliverAsync(long deliveryId)
        => PostWithoutBodyBoolAsync($"/api/webhooks/deliveries/{deliveryId}/redeliver");
}
