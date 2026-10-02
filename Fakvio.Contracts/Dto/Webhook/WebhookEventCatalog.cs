namespace Fakvio.Contracts.Dto.Webhook;

/// <summary>
/// Event name constants for outbound webhooks (v1 catalog, DEVGUIDE §4.x Webhooks).
/// Shared between the publisher (Infrastructure), the subscription CRUD validation (API),
/// and the UI event-picker checkboxes — keep this the single list of valid event names.
/// </summary>
public static class WebhookEventCatalog
{
    public const string InvoiceCreated = "invoice.created";
    public const string InvoiceSent = "invoice.sent";
    public const string InvoicePaid = "invoice.paid";
    public const string InvoiceCancelled = "invoice.cancelled";
    public const string ReceivedInvoiceCreated = "received_invoice.created";
    public const string PaymentReceived = "payment.received";

    /// <summary>Sent by the "test" button — not a real business event, so it is NOT in <see cref="All"/> (can't be subscribed to on its own; every active subscription receives a ping test regardless of its Events list).</summary>
    public const string Ping = "ping";

    /// <summary>All subscribable event names, for UI checkboxes and server-side validation.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        InvoiceCreated, InvoiceSent, InvoicePaid, InvoiceCancelled, ReceivedInvoiceCreated, PaymentReceived,
    };
}
