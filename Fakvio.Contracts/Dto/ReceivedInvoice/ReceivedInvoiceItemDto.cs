namespace Fakvio.Contracts.Dto.ReceivedInvoice;

/// <summary>
/// DTO for a received invoice line item (response).
/// </summary>
public class ReceivedInvoiceItemDto
{
    public long Id { get; set; }
    public int OrderIndex { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public long? VatRateId { get; set; }
    public decimal VatRatePercentage { get; set; }
    public decimal TotalBeforeVat { get; set; }
    public decimal VatAmount { get; set; }
    public decimal TotalWithVat { get; set; }
    public string? ProductCode { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// DTO for creating a received invoice line item.
/// </summary>
public class CreateReceivedInvoiceItemDto
{
    public int OrderIndex { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = "pcs";
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// Optional VatRate reference. If provided, percentage is fetched from entity.
    /// </summary>
    public long? VatRateId { get; set; }

    /// <summary>
    /// VAT rate percentage — used if VatRateId is not provided.
    /// </summary>
    public decimal VatRatePercentage { get; set; }

    public string? ProductCode { get; set; }
    public string? Notes { get; set; }
}
