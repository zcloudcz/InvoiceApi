using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.Invoice;

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
    /// <summary>
    /// Client's display color (CSS hex) for row highlighting in grids. Null = default.
    /// </summary>
    public string? ClientColor { get; set; }

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

    /// <summary>
    /// Sum of all PaymentMatch rows linked to this invoice.
    /// Maintained server-side by PaymentMatchingService — clients should treat as read-only.
    /// </summary>
    public decimal PaidAmount { get; set; }

    /// <summary>
    /// ISO2 destination country when this is an EU OSS invoice (B2C sale to a consumer
    /// in another EU state by an OSS-registered issuer). Null for ordinary invoices.
    /// Computed server-side — read-only, see Invoice.OssCountryCode and DEVGUIDE §4.16.
    /// </summary>
    public string? OssCountryCode { get; set; }

    /// <summary>CZK per one unit of the document currency (ČNB). Null for CZK / not determined. Read-only here; see Invoice.ExchangeRate.</summary>
    public decimal? ExchangeRate { get; set; }

    /// <summary>Date of the ČNB fixing the rate comes from; null = manual or none.</summary>
    public DateOnly? ExchangeRateDate { get; set; }

    public List<InvoiceItemDto> InvoiceItem { get; set; } = new();

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
