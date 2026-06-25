using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.InvoiceEmail;

/// <summary>
/// Read-model for an inbound invoice email in the inbox list.
/// </summary>
public class InboundInvoiceEmailDto
{
    public long Id { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string? FromDisplayName { get; set; }
    public string? Subject { get; set; }
    public DateTime? EmailDate { get; set; }
    public DateTime ServerReceivedAt { get; set; }

    public EInvoiceEmailStatus Status { get; set; }
    public EInvoiceDirection? Direction { get; set; }
    public decimal? ClassificationConfidence { get; set; }
    public string? StatusError { get; set; }

    public long? ReceivedInvoiceId { get; set; }
    public long? InvoiceId { get; set; }

    public int AttachmentCount { get; set; }
    public bool HasPdf { get; set; }
    public bool HasIsdoc { get; set; }
}
