namespace Fakvio.Contracts.Dto.Webhook;

/// <summary>Read DTO for a single webhook delivery attempt chain — the "deliveries log" shown in the UI.</summary>
public class WebhookDeliveryDto
{
    public long Id { get; set; }
    public long SubscriptionId { get; set; }
    public Guid EventId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}

/// <summary>Document summary embedded in the webhook payload's "data" field (invoice or received invoice).</summary>
public class WebhookDocumentSummaryDto
{
    public long Id { get; set; }
    public string? Number { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string? ClientIco { get; set; }
    public decimal Total { get; set; }
    public string Currency { get; set; } = string.Empty;
    public DateTime? DueDate { get; set; }
    public DateTime? PaidAt { get; set; }
}
