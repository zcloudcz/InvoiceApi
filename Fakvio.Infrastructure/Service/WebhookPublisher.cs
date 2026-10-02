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
    public async Task PublishAsync(string eventType, object data, CancellationToken ct = default, bool save = true)
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

            if (save)
                await _context.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Detach the outbox rows we added: left in the change tracker, they would be retried
            // (and fail again) by the caller's NEXT SaveChanges and poison the business operation.
            try
            {
                foreach (var entry in _context.ChangeTracker.Entries<WebhookDelivery>()
                             .Where(e => e.State == EntityState.Added).ToList())
                    entry.State = EntityState.Detached;
            }
            catch (ObjectDisposedException) { /* context already gone - nothing to clean */ }

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
    public async Task PublishInvoiceEventAsync(string eventType, Invoice invoice, bool save, CancellationToken ct = default)
    {
        try
        {
            await PublishAsync(eventType, await ToSummaryAsync(invoice, ct), ct, save);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebhookPublisher: failed to build payload for {EventType}", eventType);
        }
    }

    /// <inheritdoc />
    public async Task PublishPaymentReceivedAsync(Invoice invoice, decimal amount, DateTime matchedAt, bool save, CancellationToken ct = default)
    {
        try
        {
            var summary = await ToSummaryAsync(invoice, ct);
            await PublishAsync(WebhookEventCatalog.PaymentReceived, new { invoice = summary, amount, matchedAt }, ct, save);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebhookPublisher: failed to build payment payload");
        }
    }

    /// <summary>
    /// Summary from the in-memory entity (status/paid date may be unsaved); client and currency
    /// names are looked up by id because the entity's navigations are not always loaded.
    /// </summary>
    private async Task<WebhookDocumentSummaryDto> ToSummaryAsync(Invoice invoice, CancellationToken ct)
    {
        var client = await _context.Client.AsNoTracking().Where(c => c.Id == invoice.ClientId)
            .Select(c => new { c.CompanyName, c.RegistrationNumber }).FirstOrDefaultAsync(ct);
        var currency = await _context.Currency.AsNoTracking().Where(c => c.Id == invoice.CurrencyId)
            .Select(c => c.Code).FirstOrDefaultAsync(ct);

        return new WebhookDocumentSummaryDto
        {
            Id = invoice.Id,
            Number = invoice.DocumentNumber,
            Type = invoice.DocumentType.ToString(),
            Status = invoice.Status.ToString(),
            ClientName = client?.CompanyName ?? string.Empty,
            ClientIco = client?.RegistrationNumber,
            Total = invoice.TotalWithVat,
            Currency = currency ?? string.Empty,
            DueDate = invoice.DueDate,
            PaidAt = invoice.PaidAt,
        };
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
