namespace InvoiceApi.Application.Dto.Invoice;

/// <summary>
/// DTO for invoice item (line item)
/// </summary>
public class InvoiceItemDto
{
    public long Id { get; set; }
    public int OrderIndex { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// VAT rate ID (reference to VatRate entity)
    /// Required when issuer is VAT payer
    /// </summary>
    public long? VatRateId { get; set; }

    /// <summary>
    /// VAT rate percentage (stored value at time of invoice creation)
    /// Example: 21, 15, 10, 0
    /// </summary>
    public decimal VatRatePercentage { get; set; }

    public decimal TotalBeforeVat { get; set; }
    public decimal VatAmount { get; set; }
    public decimal TotalWithVat { get; set; }
    public string? ProductCode { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// DTO for creating an invoice item
/// </summary>
public class CreateInvoiceItemDto
{
    /// <summary>
    /// Order/position on invoice (1 = first, 2 = second, etc.)
    /// If not provided, items are ordered as received
    /// </summary>
    public int OrderIndex { get; set; }

    /// <summary>
    /// Description of product/service
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Quantity
    /// Example: 5, 10.5, 2.25
    /// </summary>
    public decimal Quantity { get; set; }

    /// <summary>
    /// Unit of measurement
    /// Example: "pcs", "hours", "kg"
    /// </summary>
    public string Unit { get; set; } = "pcs";

    /// <summary>
    /// Price per one unit (before VAT)
    /// </summary>
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// VAT rate ID (reference to VatRate entity)
    /// Required when issuer is VAT payer
    /// If provided, the rate percentage will be fetched from VatRate entity
    /// </summary>
    public long? VatRateId { get; set; }

    /// <summary>
    /// VAT rate as percentage (optional if VatRateId is provided)
    /// Example: 21, 15, 10, 0
    /// If VatRateId is provided, this value will be overridden from the VatRate entity
    /// If VatRateId is not provided, this value must be set manually
    /// </summary>
    public decimal VatRatePercentage { get; set; }

    /// <summary>
    /// Optional product code for linking to product catalog
    /// </summary>
    public string? ProductCode { get; set; }

    /// <summary>
    /// Optional notes for this specific item
    /// </summary>
    public string? Notes { get; set; }
}

/// <summary>
/// DTO for updating an invoice item
/// </summary>
public class UpdateInvoiceItemDto
{
    public int? OrderIndex { get; set; }
    public string? Description { get; set; }
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public decimal? UnitPrice { get; set; }

    /// <summary>
    /// VAT rate ID (reference to VatRate entity)
    /// </summary>
    public long? VatRateId { get; set; }

    /// <summary>
    /// VAT rate percentage
    /// </summary>
    public decimal? VatRatePercentage { get; set; }

    public string? ProductCode { get; set; }
    public string? Notes { get; set; }
}
