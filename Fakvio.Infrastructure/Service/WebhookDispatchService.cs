using Fakvio.Application.Service;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <inheritdoc cref="IWebhookDispatchService"/>
public class WebhookDispatchService : IWebhookDispatchService
{
    /// <summary>Name of the named HttpClient configured with the SSRF-guarded SocketsHttpHandler (see ServiceCollectionExtensions).</summary>
    public const string HttpClientName = "WebhookDispatch";

    /// <summary>
    /// Retry schedule (DEVGUIDE §4.15 Webhooks): delay before each successive attempt.
    /// Index 0 = delay before the 2nd attempt (the 1st happens as soon as the event is enqueued).
    /// After the last entry is exhausted (8 total attempts), the delivery is marked Failed.
    /// </summary>
    private static readonly TimeSpan[] RetryBackoff =
    {
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(12),
        TimeSpan.FromHours(24),
    };

    private const int MaxResponseBodyBytes = 4096;
    private const int MaxErrorLength = 2000;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(30);

    private readonly TenantDbContext _context;
    private readonly ICredentialProtector _credentialProtector;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebhookDispatchService> _logger;

    public WebhookDispatchService(
        TenantDbContext context,
        ICredentialProtector credentialProtector,
        IHttpClientFactory httpClientFactory,
        ILogger<WebhookDispatchService> logger)
    {
        _context = context;
        _credentialProtector = credentialProtector;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RunCycleAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        var due = await _context.WebhookDelivery
            .Include(d => d.Subscription)
            .Where(d => d.Status == EWebhookDeliveryStatus.Pending && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt)
            .Take(100) // Bound the cycle — a huge backlog is spread across several ticks rather than blocking one.
            .ToListAsync(ct);

        // ponytail: deliveries are sent serially (<= 100 per tenant per cycle, 10 s timeout each), so a
        // tenant with many slow endpoints can lag; add bounded parallelism per subscription if needed.
        foreach (var delivery in due)
        {
            ct.ThrowIfCancellationRequested();
            await AttemptDeliveryAsync(delivery, ct);
        }

        await CleanupOldDeliveriesAsync(ct);
    }

    private async Task AttemptDeliveryAsync(Domain.Entities.WebhookDelivery delivery, CancellationToken ct)
    {
        delivery.Attempts++;

        if (!delivery.Subscription.IsActive)
        {
            // Subscription was paused/deleted-equivalent between enqueue and send — stop retrying.
            delivery.Status = EWebhookDeliveryStatus.Failed;
            delivery.LastError = "Subscription is no longer active.";
            await _context.SaveChangesAsync(ct);
            return;
        }

        try
        {
            var secret = _credentialProtector.Decrypt(delivery.Subscription.SecretEncrypted) ?? string.Empty;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var signature = WebhookSigner.Sign(secret, timestamp, delivery.PayloadJson);

            using var request = new HttpRequestMessage(HttpMethod.Post, delivery.Subscription.Url)
            {
                Content = new StringContent(delivery.PayloadJson, System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Fakvio-Webhook-Id", delivery.EventId.ToString());
            request.Headers.Add("Fakvio-Webhook-Timestamp", timestamp.ToString());
            request.Headers.Add("Fakvio-Webhook-Signature", signature);

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            await ReadCappedBodyAsync(response, cts.Token); // Drain (capped) so the connection can be reused; body content itself isn't needed.

            delivery.LastStatusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                delivery.Status = EWebhookDeliveryStatus.Succeeded;
                delivery.DeliveredAt = DateTimeOffset.UtcNow;
                delivery.LastError = null;
            }
            else
            {
                ScheduleRetryOrFail(delivery, $"Endpoint returned HTTP {(int)response.StatusCode}.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Includes timeouts, connection refused, TLS failures, and SSRF rejections raised by
            // WebhookUrlGuard.ConnectCallback — all are just "delivery failed", retried the same way.
            ScheduleRetryOrFail(delivery, ex.GetBaseException().Message);
        }

        await _context.SaveChangesAsync(ct);
    }

    /// <summary>Advances NextAttemptAt by the backoff schedule, or marks Failed once attempts exhaust it.</summary>
    private void ScheduleRetryOrFail(Domain.Entities.WebhookDelivery delivery, string error)
    {
        delivery.LastError = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;

        // Attempts is 1-based (incremented before the send). RetryBackoff[0] is the delay
        // before attempt #2, so index = Attempts - 1.
        var backoffIndex = delivery.Attempts - 1;
        if (backoffIndex < RetryBackoff.Length)
        {
            delivery.Status = EWebhookDeliveryStatus.Pending;
            delivery.NextAttemptAt = DateTimeOffset.UtcNow.Add(RetryBackoff[backoffIndex]);
        }
        else
        {
            delivery.Status = EWebhookDeliveryStatus.Failed;
        }
    }

    private static async Task ReadCappedBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[MaxResponseBodyBytes];
            var total = 0;
            int read;
            while (total < buffer.Length &&
                   (read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)) > 0)
            {
                total += read;
            }
        }
        catch
        {
            // Body is informational only (never surfaced) — a read failure must not fail the delivery.
        }
    }

    private async Task CleanupOldDeliveriesAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - RetentionPeriod;

        // Bounded batch per cycle (the cycle runs every minute, so a big backlog drains quickly);
        // plain Remove instead of ExecuteDelete keeps this testable with the in-memory provider.
        var expired = await _context.WebhookDelivery
            .Where(d => d.CreatedAt < cutoff &&
                        (d.Status == EWebhookDeliveryStatus.Succeeded || d.Status == EWebhookDeliveryStatus.Failed))
            .Take(1000)
            .ToListAsync(ct);

        if (expired.Count == 0) return;

        _context.WebhookDelivery.RemoveRange(expired);
        await _context.SaveChangesAsync(ct);
    }
}
