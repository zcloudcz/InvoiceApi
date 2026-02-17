using InvoiceApi.Domain.Enums;

namespace InvoiceApi.Contracts.Dto.Invoice;

/// <summary>
/// DTO for invoice data
/// Used for API responses
/// </summary>
public class InvoiceDto
{
    public long Id { get; set; }
    public EDocumentType DocumentType { get; set; }
    public EInvoiceStatus Status { get; set; }
    public string? DocumentNumber { get; set; }
    public DateTime? IssueDate { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? TaxableSupplyDate { get; set; }

    public long? ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;

    public long IssuerId { get; set; }
    public string IssuerName { get; set; } = string.Empty;

    public long? OriginalInvoiceId { get; set; }
    public string? OriginalInvoiceNumber { get; set; }

    public string? VariableSymbol { get; set; }
    public string? ConstantSymbol { get; set; }
    public string? SpecificSymbol { get; set; }

    public string? BankAccountNumber { get; set; }
    public string? IBAN { get; set; }
    public string? SWIFT { get; set; }
    /// <summary>
    /// Payment method — enum for type safety and localized display
    /// </summary>
    public EPaymentMethod? PaymentMethod { get; set; }

    public decimal TotalBeforeVat { get; set; }
    public decimal TotalVat { get; set; }
    public decimal TotalWithVat { get; set; }

    // Currency information
    public long CurrencyId { get; set; }
    public string CurrencyCode { get; set; } = string.Empty; // e.g., "CZK"
    public string CurrencySymbol { get; set; } = string.Empty; // e.g., "Kč"

    public string? Notes { get; set; }

    public bool IsExported { get; set; }
    public DateTime? LastExportedAt { get; set; }
    public bool IsSentByEmail { get; set; }
    public DateTime? LastSentByEmailAt { get; set; }
    public DateTime? PaidAt { get; set; }

    public List<InvoiceItemDto> InvoiceItem { get; set; } = new();

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
