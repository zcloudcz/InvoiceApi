using System.Text.Json;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Webhook;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// CRUD + test/redeliver for webhook subscriptions (DEVGUIDE §4.15 Webhooks).
/// Secrets are encrypted at rest with <see cref="ICredentialProtector"/> — same pattern as
/// SMTP/IMAP passwords (DEVGUIDE §2.6) — and only ever returned in plaintext right after
/// Create/Rotate.
/// </summary>
public class WebhookSubscriptionService : IWebhookSubscriptionService
{
    /// <summary>Response body is capped — a misbehaving endpoint must not let us buffer an unbounded amount of memory.</summary>
    private const int MaxResponseBodyBytes = 4096;

    private readonly TenantDbContext _context;
    private readonly ICredentialProtector _credentialProtector;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHostEnvironment _env;
    private readonly ILogger<WebhookSubscriptionService> _logger;

    public WebhookSubscriptionService(
        TenantDbContext context,
        ICredentialProtector credentialProtector,
        IHttpClientFactory httpClientFactory,
        IHostEnvironment env,
        ILogger<WebhookSubscriptionService> logger)
    {
        _context = context;
        _credentialProtector = credentialProtector;
        _httpClientFactory = httpClientFactory;
        _env = env;
        _logger = logger;
    }

    public async Task<List<WebhookSubscriptionDto>> GetAllAsync(CancellationToken ct = default)
    {
        var entities = await _context.WebhookSubscription
            .AsNoTracking()
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

        return entities.Select(MapToDto).ToList();
    }

    public async Task<WebhookSubscriptionDto?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        var entity = await _context.WebhookSubscription.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        return entity == null ? null : MapToDto(entity);
    }

    public async Task<WebhookSubscriptionCreatedDto> CreateAsync(CreateWebhookSubscriptionDto dto, CancellationToken ct = default)
    {
        var uri = ValidateAndParseUrl(dto.Url);
        var events = ValidateEvents(dto.Events);

        var secret = WebhookSigner.GenerateSecret();
        var entity = new WebhookSubscription
        {
            Url = uri.ToString(),
            Description = dto.Description,
            Events = events,
            SecretEncrypted = _credentialProtector.Encrypt(secret) ?? string.Empty,
            IsActive = true,
        };

        _context.WebhookSubscription.Add(entity);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Created webhook subscription {Id} for {Url}", entity.Id, entity.Url);

        return new WebhookSubscriptionCreatedDto { Subscription = MapToDto(entity), Secret = secret };
    }

    public async Task<WebhookSubscriptionDto> UpdateAsync(long id, UpdateWebhookSubscriptionDto dto, CancellationToken ct = default)
    {
        var entity = await _context.WebhookSubscription.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new InvalidOperationException($"Webhook subscription {id} not found.");

        var uri = ValidateAndParseUrl(dto.Url);
        entity.Url = uri.ToString();
        entity.Description = dto.Description;
        entity.Events = ValidateEvents(dto.Events);
        entity.IsActive = dto.IsActive;

        await _context.SaveChangesAsync(ct);
        return MapToDto(entity);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        var entity = await _context.WebhookSubscription.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new InvalidOperationException($"Webhook subscription {id} not found.");

        _context.WebhookSubscription.Remove(entity);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<WebhookSubscriptionCreatedDto> RotateSecretAsync(long id, CancellationToken ct = default)
    {
        var entity = await _context.WebhookSubscription.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new InvalidOperationException($"Webhook subscription {id} not found.");

        var secret = WebhookSigner.GenerateSecret();
        entity.SecretEncrypted = _credentialProtector.Encrypt(secret) ?? string.Empty;
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Rotated secret for webhook subscription {Id}", id);

        return new WebhookSubscriptionCreatedDto { Subscription = MapToDto(entity), Secret = secret };
    }

    public async Task<WebhookTestResultDto> TestAsync(long id, CancellationToken ct = default)
    {
        var entity = await _context.WebhookSubscription.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new InvalidOperationException($"Webhook subscription {id} not found.");

        var secret = _credentialProtector.Decrypt(entity.SecretEncrypted) ?? string.Empty;
        var payload = new
        {
            id = Guid.NewGuid(),
            type = WebhookEventCatalog.Ping,
            createdAt = DateTimeOffset.UtcNow,
            data = new { message = "This is a test event from Fakvio." },
        };
        var body = JsonSerializer.Serialize(payload);

        try
        {
            var client = _httpClientFactory.CreateClient(WebhookDispatchService.HttpClientName);
            using var request = BuildSignedRequest(entity.Url, body, secret);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            return new WebhookTestResultDto
            {
                Success = response.IsSuccessStatusCode,
                StatusCode = (int)response.StatusCode,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Webhook test failed for subscription {Id}", id);
            return new WebhookTestResultDto { Success = false, Error = ex.Message };
        }
    }

    public async Task<List<WebhookDeliveryDto>> GetDeliveriesAsync(long subscriptionId, CancellationToken ct = default)
    {
        var entities = await _context.WebhookDelivery
            .AsNoTracking()
            .Where(d => d.SubscriptionId == subscriptionId)
            .OrderByDescending(d => d.CreatedAt)
            .Take(200)
            .ToListAsync(ct);

        return entities.Select(d => new WebhookDeliveryDto
        {
            Id = d.Id,
            SubscriptionId = d.SubscriptionId,
            EventId = d.EventId,
            EventType = d.EventType,
            Status = d.Status.ToString(),
            Attempts = d.Attempts,
            NextAttemptAt = d.NextAttemptAt,
            LastStatusCode = d.LastStatusCode,
            LastError = d.LastError,
            CreatedAt = d.CreatedAt,
            DeliveredAt = d.DeliveredAt,
        }).ToList();
    }

    public async Task RedeliverAsync(long deliveryId, CancellationToken ct = default)
    {
        var delivery = await _context.WebhookDelivery.FirstOrDefaultAsync(d => d.Id == deliveryId, ct)
            ?? throw new InvalidOperationException($"Webhook delivery {deliveryId} not found.");

        delivery.Status = EWebhookDeliveryStatus.Pending;
        delivery.Attempts = 0;
        delivery.NextAttemptAt = DateTimeOffset.UtcNow;
        delivery.LastError = null;
        delivery.LastStatusCode = null;

        await _context.SaveChangesAsync(ct);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private Uri ValidateAndParseUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Webhook URL is not a valid absolute URL.");

        WebhookUrlGuard.ValidateScheme(uri, _env);
        return uri;
    }

    private static List<string> ValidateEvents(List<string> events)
    {
        if (events.Count == 0)
            throw new InvalidOperationException("Select at least one webhook event.");

        var invalid = events.Where(e => !WebhookEventCatalog.All.Contains(e)).ToList();
        if (invalid.Count > 0)
            throw new InvalidOperationException($"Unknown webhook event(s): {string.Join(", ", invalid)}");

        return events.Distinct().ToList();
    }

    private static HttpRequestMessage BuildSignedRequest(string url, string body, string secret)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = WebhookSigner.Sign(secret, timestamp, body);

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Fakvio-Webhook-Id", Guid.NewGuid().ToString());
        request.Headers.Add("Fakvio-Webhook-Timestamp", timestamp.ToString());
        request.Headers.Add("Fakvio-Webhook-Signature", signature);
        return request;
    }

    private static WebhookSubscriptionDto MapToDto(WebhookSubscription entity) => new()
    {
        Id = entity.Id,
        Url = entity.Url,
        Description = entity.Description,
        Events = entity.Events,
        IsActive = entity.IsActive,
        CreatedAt = entity.CreatedAt,
    };
}
