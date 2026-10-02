using Fakvio.Domain.Enums;

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

    /// <summary>
    /// VAT accounting regime for this line item. Default Standard — backward-compatible.
    /// </summary>
    public EVatRegime VatRegime { get; set; } = EVatRegime.Standard;

    /// <summary>
    /// FK to ReverseChargeCode lookup. Set when VatRegime == ReverseCharge.
    /// </summary>
    public long? ReverseChargeCodeId { get; set; }

    /// <summary>
    /// Self-assessed VAT (base * rate) for ReverseCharge items — see
    /// <see cref="Fakvio.Domain.Entities.ReceivedInvoiceItem.InformationalVatAmount"/>.
    /// Always 0 for Standard/Exempt/OutOfScope items.
    /// </summary>
    public decimal InformationalVatAmount { get; set; }

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

    /// <summary>
    /// VAT accounting regime for this line item. Default Standard — backward-compatible
    /// with all existing received-invoice creation flows.
    /// Set to ReverseCharge when WE must self-assess the VAT (§92a ZDPH); in that case
    /// ReverseChargeCodeId must also be set.
    /// </summary>
    public EVatRegime VatRegime { get; set; } = EVatRegime.Standard;

    /// <summary>
    /// FK to ReverseChargeCode lookup. Required when VatRegime == ReverseCharge,
    /// must be null for Standard/Exempt/OutOfScope.
    /// </summary>
    public long? ReverseChargeCodeId { get; set; }

    public string? ProductCode { get; set; }
    public string? Notes { get; set; }
}
