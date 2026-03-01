using Fakvio.Domain.Common;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Represents a VAT (Value Added Tax) rate configuration
/// In Czech: Sazba DPH (Daň z přidané hodnoty)
/// Defines tax rates that can be applied to invoice items
/// Different rates may be valid during different time periods
/// </summary>
public class VatRate : BaseEntity
{
    /// <summary>
    /// Name/description of this VAT rate
    /// Example: "DPH 21% standardní", "DPH 12% snížená"
    /// Helps users identify the rate in the UI
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The actual tax rate as a percentage
    /// Example: 21.00 for 21%, 12.00 for 12%, 0.00 for tax-free
    /// Stored as decimal for precise calculations
    /// </summary>
    public decimal Rate { get; set; }

    /// <summary>
    /// When does this rate become valid (start date)
    /// VAT rates can change over time by law
    /// Example: New rate might be valid from 2025-01-01
    /// </summary>
    public DateTime ValidFrom { get; set; }

    /// <summary>
    /// When does this rate stop being valid (end date)
    /// Null = valid indefinitely (until further notice)
    /// When rate changes, old rate gets ValidTo date, new rate is created
    /// Example: Old 21% rate valid until 2024-12-31, new 23% rate from 2025-01-01
    /// </summary>
    public DateTime? ValidTo { get; set; }

    /// <summary>
    /// Is this a reduced VAT rate?
    /// In Czech Republic: standard rate (21%) vs reduced rate (12% or 10%)
    /// Reduced rates apply to specific goods/services (food, books, etc.)
    /// False = standard rate, True = reduced rate
    /// </summary>
    public bool IsReduced { get; set; }

    /// <summary>
    /// Is this the default rate to use?
    /// When creating invoice items, default rate can be pre-selected
    /// Business rule: Can have max ONE default standard rate and ONE default reduced rate
    /// - IsDefault=true, IsReduced=false = default standard rate (only one allowed)
    /// - IsDefault=true, IsReduced=true = default reduced rate (only one allowed)
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Is this rate currently active and available for use?
    /// Inactive rates are hidden from selection but preserved for historical invoices
    /// Set to false instead of deleting to maintain data integrity
    /// </summary>
    public bool IsActive { get; set; } = true;

    // Navigation properties

    /// <summary>
    /// Collection of invoice items using this VAT rate
    /// Used to track which invoices applied this rate
    /// Important for reporting and auditing
    /// </summary>
    public ICollection<InvoiceItem> InvoiceItems { get; set; } = new List<InvoiceItem>();
}
