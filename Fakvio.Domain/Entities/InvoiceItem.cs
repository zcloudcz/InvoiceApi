using Fakvio.Domain.Common;
using Fakvio.Domain.Enums;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Represents one line item on an invoice
/// Each item is a product or service being billed
/// Multiple items can exist on one invoice
/// </summary>
public class InvoiceItem : BaseEntity
{
    /// <summary>
    /// Foreign key to Invoice
    /// Which invoice does this item belong to
    /// </summary>
    public long InvoiceId { get; set; }

    /// <summary>
    /// Navigation property to Invoice
    /// </summary>
    public Invoice Invoice { get; set; } = null!;

    /// <summary>
    /// Order of this item on the invoice (for display)
    /// 1 = first item, 2 = second item, etc.
    /// Used to maintain consistent ordering
    /// </summary>
    public int OrderIndex { get; set; }

    /// <summary>
    /// Description of the product/service
    /// Example: "Website development", "Consulting hours", "Product XYZ"
    /// This is what the client sees on the invoice
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Quantity of this item
    /// Example: 5 pieces, 10 hours, 2.5 kg
    /// Can be decimal for items sold by weight/volume
    /// </summary>
    public decimal Quantity { get; set; }

    /// <summary>
    /// Unit of measurement
    /// Examples: "pcs" (pieces), "hours", "kg", "m²"
    /// Displayed next to quantity
    /// </summary>
    public string Unit { get; set; } = "pcs";

    /// <summary>
    /// Price per one unit (before VAT)
    /// Example: If selling 5 items at 100 CZK each, UnitPrice = 100
    /// </summary>
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// Foreign key to VatRate configuration
    /// Links to the VAT rate that was applied to this item
    /// Required when issuer is VAT payer, null otherwise
    /// </summary>
    public long? VatRateId { get; set; }

    /// <summary>
    /// Navigation property to VatRate configuration
    /// The VAT rate definition that was used for this item
    /// </summary>
    public VatRate? VatRate { get; set; }

    /// <summary>
    /// VAT rate percentage stored at time of invoice creation
    /// Examples: 21, 15, 10, 0
    /// In Czech Republic: standard rate is 21%, reduced 12%/10%
    /// 0 for non-VAT invoices
    /// Stored separately from VatRate entity to preserve exact rate even if rate definition changes
    /// This is the value used in calculations
    /// </summary>
    public decimal VatRatePercentage { get; set; }

    /// <summary>
    /// Total price before VAT for this item
    /// Calculated as: Quantity * UnitPrice
    /// Example: 5 pieces * 100 CZK = 500 CZK
    /// </summary>
    public decimal TotalBeforeVat { get; set; }

    /// <summary>
    /// VAT amount for this item
    /// Calculated as: TotalBeforeVat * (VatRatePercentage / 100)
    /// Example: 500 CZK * 21% = 105 CZK
    /// </summary>
    public decimal VatAmount { get; set; }

    /// <summary>
    /// Total price including VAT for this item
    /// Calculated as: TotalBeforeVat + VatAmount
    /// Example: 500 + 105 = 605 CZK
    /// This is what this line contributes to invoice total
    /// </summary>
    public decimal TotalWithVat { get; set; }

    /// <summary>
    /// When true, this row is a text-only note (e.g., "Práce provedeny dle smlouvy č. 123").
    /// Text rows are displayed in the invoice item table but excluded from totals calculation.
    /// All numeric fields (Quantity, UnitPrice, TotalBeforeVat, etc.) are ignored for text rows.
    /// Only Description is used.
    /// </summary>
    public bool IsTextRow { get; set; }

    /// <summary>
    /// VAT accounting regime for this line item.
    /// Controls how VAT is treated: standard charge, reverse charge (PDP §92a–92e ZDPH),
    /// exempt, or out-of-scope.
    /// Default is <see cref="EVatRegime.Standard"/> — backward-compatible with all existing items.
    /// Stored as NOT NULL integer in the database (default 0 = Standard).
    /// </summary>
    public EVatRegime VatRegime { get; set; } = EVatRegime.Standard;

    /// <summary>
    /// Foreign key to ReverseChargeCode lookup table.
    /// Required when VatRegime == ReverseCharge; must be null for all other regimes.
    /// The code identifies the type of supply (predmět plnění) as defined by MFČR
    /// and is used in the VAT control statement (kontrolní hlášení / EPO XML).
    /// Nullable because Standard/Exempt/OutOfScope items do not have a reverse charge code.
    /// </summary>
    public long? ReverseChargeCodeId { get; set; }

    /// <summary>
    /// Navigation property to ReverseChargeCode lookup.
    /// Loaded when the FK is set (VatRegime == ReverseCharge items).
    /// Delete behaviour is Restrict — the lookup is a reference data table; deleting
    /// a code while invoices reference it would corrupt historical data.
    /// </summary>
    public ReverseChargeCode? ReverseChargeCode { get; set; }

    /// <summary>
    /// Informational VAT amount for Reverse Charge items.
    /// For ReverseCharge regime: the invoice does NOT bill VAT (TotalWithVat = TotalBeforeVat),
    /// but Czech law (§92a ZDPH) requires the rate and calculated VAT to appear on the document
    /// so the buyer can self-assess. This property stores that informational value.
    /// For all other regimes this is always 0 — the actual billed amount is in VatAmount.
    /// Persisted to the database (EF column with HasPrecision). InvoiceService sets it
    /// during Create/Update so PDF/ISDOC exports can read it without re-running the full calculation.
    /// </summary>
    public decimal InformationalVatAmount { get; set; }

    /// <summary>
    /// Optional product/service code
    /// For linking to product catalog or accounting codes
    /// Example: "SVC-001", "PROD-123"
    /// </summary>
    public string? ProductCode { get; set; }

    /// <summary>
    /// Optional additional notes for this specific item
    /// Example: "Includes setup fee", "Discount applied"
    /// </summary>
    public string? Notes { get; set; }
}
