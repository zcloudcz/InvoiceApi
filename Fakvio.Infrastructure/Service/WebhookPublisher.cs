using System.Text.Json;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Webhook;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <inheritdoc cref="IWebhookPublisher"/>
public class WebhookPublisher : IWebhookPublisher
{
    private readonly TenantDbContext _context;
    private readonly ILogger<WebhookPublisher> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public WebhookPublisher(TenantDbContext context, ILogger<WebhookPublisher> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task PublishAsync(string eventType, object data, CancellationToken ct = default)
    {
        try
        {
            // Ping is special — TestAsync (WebhookSubscriptionService) sends it synchronously
            // to one subscription and never goes through the queue, so it never reaches here.
            var subscriptionIds = await _context.WebhookSubscription
                .AsNoTracking()
                .Where(s => s.IsActive)
                .Select(s => new { s.Id, s.Events })
                .ToListAsync(ct);

            var matching = subscriptionIds.Where(s => s.Events.Contains(eventType)).Select(s => s.Id).ToList();
            if (matching.Count == 0)
                return; // No subscribers — the common case, must stay cheap and silent.

            // Company id = the number in the tenant schema name ("tenant_42" -> 42). Works the same in
            // request scope and in background jobs (where ITenantResolver has no current user).
            long? companyId = long.TryParse(_context.Schema?.Replace("tenant_", ""), out var cid) ? cid : null;

            var eventId = Guid.NewGuid();
            var payload = new
            {
                id = eventId,
                type = eventType,
                createdAt = DateTimeOffset.UtcNow,
                companyId,
                data,
            };
            var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
            var now = DateTimeOffset.UtcNow;

            foreach (var subscriptionId in matching)
            {
                _context.WebhookDelivery.Add(new WebhookDelivery
                {
                    SubscriptionId = subscriptionId,
                    EventId = eventId,
                    EventType = eventType,
                    PayloadJson = payloadJson,
                    Status = EWebhookDeliveryStatus.Pending,
                    Attempts = 0,
                    NextAttemptAt = now,
                    CreatedAt = now.UtcDateTime,
                });
            }

            await _context.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A webhook enqueue failure must never break the business operation that triggered
            // it (invoice creation/payment/etc. already succeeded) — log and move on.
            _logger.LogError(ex, "WebhookPublisher: failed to enqueue event {EventType}", eventType);
        }
    }

    /// <inheritdoc />
    public async Task PublishInvoiceEventAsync(string eventType, long invoiceId, CancellationToken ct = default)
    {
        var summary = await BuildInvoiceSummaryAsync(invoiceId, ct);
        if (summary == null)
            return;

        await PublishAsync(eventType, summary, ct);
    }

    /// <inheritdoc />
    public async Task PublishReceivedInvoiceEventAsync(string eventType, long receivedInvoiceId, CancellationToken ct = default)
    {
        var invoice = await _context.ReceivedInvoice
            .AsNoTracking()
            .Include(r => r.Supplier)
            .Include(r => r.Currency)
            .FirstOrDefaultAsync(r => r.Id == receivedInvoiceId, ct);

        if (invoice == null)
            return;

        var summary = new WebhookDocumentSummaryDto
        {
            Id = invoice.Id,
            Number = invoice.DocumentNumber,
            Type = "ReceivedInvoice",
            Status = invoice.Status.ToString(),
            ClientName = invoice.Supplier?.CompanyName ?? string.Empty,
            ClientIco = invoice.Supplier?.RegistrationNumber,
            Total = invoice.TotalWithVat,
            Currency = invoice.Currency?.Code ?? string.Empty,
            DueDate = invoice.DueDate,
            PaidAt = invoice.PaidAt,
        };

        await PublishAsync(eventType, summary, ct);
    }

    /// <inheritdoc />
    public async Task PublishPaymentReceivedAsync(long invoiceId, decimal amount, DateTime matchedAt, CancellationToken ct = default)
    {
        var summary = await BuildInvoiceSummaryAsync(invoiceId, ct);
        if (summary == null)
            return;

        await PublishAsync(WebhookEventCatalog.PaymentReceived, new { invoice = summary, amount, matchedAt }, ct);
    }

    private async Task<WebhookDocumentSummaryDto?> BuildInvoiceSummaryAsync(long invoiceId, CancellationToken ct)
    {
        var invoice = await _context.Invoice
            .AsNoTracking()
            .Include(i => i.Client)
            .Include(i => i.Currency)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, ct);

        if (invoice == null)
            return null;

        return new WebhookDocumentSummaryDto
        {
            Id = invoice.Id,
            Number = invoice.DocumentNumber,
            Type = invoice.DocumentType.ToString(),
            Status = invoice.Status.ToString(),
            ClientName = invoice.Client?.CompanyName ?? string.Empty,
            ClientIco = invoice.Client?.RegistrationNumber,
            Total = invoice.TotalWithVat,
            Currency = invoice.Currency?.Code ?? string.Empty,
            DueDate = invoice.DueDate,
            PaidAt = invoice.PaidAt,
        };
    }
}
