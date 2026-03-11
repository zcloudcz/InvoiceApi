using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.ReceivedInvoice;

/// <summary>
/// DTO for received (incoming) invoice data.
/// Used for API responses — represents an expense from a supplier.
/// </summary>
public class ReceivedInvoiceDto
{
    public long Id { get; set; }
    public string? DocumentNumber { get; set; }
    public EReceivedInvoiceStatus Status { get; set; }

    /// <summary>
    /// Supplier who issued this invoice to us.
    /// </summary>
    public long SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;

    public DateTime? IssueDate { get; set; }
    public DateTime? ReceivedDate { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? TaxableSupplyDate { get; set; }

    public string? VariableSymbol { get; set; }

    public decimal TotalBeforeVat { get; set; }
    public decimal TotalVat { get; set; }
    public decimal TotalWithVat { get; set; }

    public long CurrencyId { get; set; }
    public string CurrencyCode { get; set; } = string.Empty;
    public string CurrencySymbol { get; set; } = string.Empty;

    public EPaymentMethod? PaymentMethod { get; set; }
    public string? BankAccountNumber { get; set; }
    public string? IBAN { get; set; }
    public string? SWIFT { get; set; }

    public DateTime? PaidAt { get; set; }
    public string? Notes { get; set; }

    /// <summary>
    /// Attachment metadata — filename of scanned/uploaded document.
    /// </summary>
    public string? AttachmentFileName { get; set; }
    public string? AttachmentContentType { get; set; }

    public List<ReceivedInvoiceItemDto> Items { get; set; } = new();

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
