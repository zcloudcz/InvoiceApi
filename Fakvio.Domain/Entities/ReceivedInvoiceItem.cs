using Fakvio.Domain.Common;
using Fakvio.Domain.Enums;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Represents one line item on a received (incoming) invoice.
/// Each item is a product or service we are being charged for.
/// Structure mirrors InvoiceItem for consistency.
/// </summary>
public class ReceivedInvoiceItem : BaseEntity
{
    /// <summary>
    /// Foreign key to ReceivedInvoice.
    /// Which received invoice does this item belong to.
    /// </summary>
    public long ReceivedInvoiceId { get; set; }

    /// <summary>
    /// Navigation property to ReceivedInvoice.
    /// </summary>
    public ReceivedInvoice ReceivedInvoice { get; set; } = null!;

    /// <summary>
    /// Display order of this item (1, 2, 3...).
    /// </summary>
    public int OrderIndex { get; set; }

    /// <summary>
    /// Description of the product/service.
    /// Example: "Webhosting 2024", "Printer paper A4"
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Quantity of this item.
    /// </summary>
    public decimal Quantity { get; set; }

    /// <summary>
    /// Unit of measurement (e.g., "pcs", "hours", "kg").
    /// </summary>
    public string Unit { get; set; } = "pcs";

    /// <summary>
    /// Price per one unit (before VAT).
    /// </summary>
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// Foreign key to VatRate configuration.
    /// Nullable — not all received invoices have itemized VAT.
    /// </summary>
    public long? VatRateId { get; set; }

    /// <summary>
    /// Navigation property to VatRate.
    /// </summary>
    public VatRate? VatRate { get; set; }

    /// <summary>
    /// VAT rate percentage at the time of invoice receipt.
    /// Stored as snapshot (e.g., 21, 12, 0).
    /// </summary>
    public decimal VatRatePercentage { get; set; }

    /// <summary>
    /// Total price before VAT: Quantity * UnitPrice.
    /// </summary>
    public decimal TotalBeforeVat { get; set; }

    /// <summary>
    /// VAT amount: TotalBeforeVat * (VatRatePercentage / 100).
    /// </summary>
    public decimal VatAmount { get; set; }

    /// <summary>
    /// Total price with VAT: TotalBeforeVat + VatAmount.
    /// </summary>
    public decimal TotalWithVat { get; set; }

    /// <summary>
    /// VAT accounting regime for this line item — mirrors <see cref="InvoiceItem.VatRegime"/>.
    /// For received invoices, <see cref="EVatRegime.ReverseCharge"/> means WE (the recipient)
    /// must self-assess the VAT the supplier did not charge (§92a ZDPH): we owe output tax
    /// AND may claim the same amount as input tax (net zero, but both sides must be reported
    /// in DPHDP3/DPHKH1 — see <c>VatReportService</c>).
    /// Default is <see cref="EVatRegime.Standard"/> — backward-compatible with all existing rows.
    /// </summary>
    public EVatRegime VatRegime { get; set; } = EVatRegime.Standard;

    /// <summary>
    /// FK to ReverseChargeCode lookup table. Required when VatRegime == ReverseCharge,
    /// must be null otherwise. Used for DPHKH1 section B.1 (kód předmětu plnění).
    /// </summary>
    public long? ReverseChargeCodeId { get; set; }

    /// <summary>
    /// Navigation property to ReverseChargeCode lookup.
    /// </summary>
    public ReverseChargeCode? ReverseChargeCode { get; set; }

    /// <summary>
    /// Self-assessed VAT amount for Reverse Charge items (base * rate).
    /// The supplier's invoice shows 0 VAT (TotalWithVat == TotalBeforeVat); this is the
    /// amount WE must self-assess as both output tax (we owe it) and input tax (we may
    /// deduct it) — see DPHDP3 rows 10/11 (output) and 43/44 (nárok na odpočet).
    /// Always 0 for Standard/Exempt/OutOfScope items (the real tax lives in VatAmount then).
    /// </summary>
    public decimal InformationalVatAmount { get; set; }

    /// <summary>
    /// Optional product/service code.
    /// </summary>
    public string? ProductCode { get; set; }

    /// <summary>
    /// Optional notes for this item.
    /// </summary>
    public string? Notes { get; set; }
}
