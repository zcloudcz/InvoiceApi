namespace Fakvio.Application.Service;

/// <summary>
/// Enqueues outbound webhook deliveries for a business event (DEVGUIDE §4.x Webhooks).
///
/// Called from the single service method that performs each state transition (invoice created,
/// marked paid, deleted, ...) — see the call sites in InvoiceService, ReceivedInvoiceService,
/// EmailService and PaymentMatchingService. Writes a <c>WebhookDelivery</c> row per matching,
/// active subscription in the SAME DbContext as the business change where feasible (poor man's
/// transactional outbox — the row commits together with the business change, so a crash between
/// them never silently drops an event).
///
/// Must NEVER throw into the caller's business flow: a webhook problem (no subscriptions,
/// DB hiccup while enqueuing) is logged and swallowed, not surfaced as a failed invoice operation.
/// </summary>
public interface IWebhookPublisher
{
    /// <summary>
    /// Enqueues <paramref name="eventType"/> (e.g. "invoice.paid") for every active subscription
    /// of the current tenant that lists it. No-ops quickly (one query) when there are no
    /// subscriptions or none match. <paramref name="data"/> is serialized as-is into the
    /// payload's "data" field.
    /// </summary>
    Task PublishAsync(string eventType, object data, CancellationToken ct = default);

    /// <summary>
    /// Convenience wrapper for invoice-related events: loads the invoice (with Client/Currency)
    /// fresh from the DB and builds the standard invoice-summary payload before calling
    /// <see cref="PublishAsync"/>. Centralizing the summary mapping here means call sites that
    /// only have the invoice Id (e.g. after a FindAsync-based mutation) don't need to pull in
    /// extra Includes just for the webhook.
    /// </summary>
    Task PublishInvoiceEventAsync(string eventType, long invoiceId, CancellationToken ct = default);

    /// <summary>Same idea as <see cref="PublishInvoiceEventAsync"/>, for received (incoming supplier) invoices.</summary>
    Task PublishReceivedInvoiceEventAsync(string eventType, long receivedInvoiceId, CancellationToken ct = default);

    /// <summary>
    /// Publishes "payment.received" for a bank transaction matched to an invoice — the invoice
    /// summary plus the matched amount. Called from PaymentMatchingService's match/confirm paths.
    /// </summary>
    Task PublishPaymentReceivedAsync(long invoiceId, decimal amount, DateTime matchedAt, CancellationToken ct = default);
}
