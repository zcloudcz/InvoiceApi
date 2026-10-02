using Fakvio.Domain.Common;
using Fakvio.Domain.Enums;

namespace Fakvio.Domain.Entities;

/// <summary>
/// VAT rate of an EU member state, for use under the EU OSS (One-Stop-Shop, "zvláštní
/// režim jednoho správního místa — režim Unie") scheme.
///
/// Unlike <see cref="VatRate"/> (which is the tenant's own, editable CZ rate list), this
/// is statutory reference data maintained centrally by SysAdmin — a Czech VAT payer
/// registered for OSS must charge the DESTINATION country's rate on B2C sales to
/// consumers in other EU states, not the Czech rate. Tenants never edit these rows.
///
/// Lives ONLY in the Master database (no per-tenant copy, unlike VatRate/Currency/
/// NumberSequenceFormat/ContentTemplate in TenantProvisioningService step 5) — see
/// DEVGUIDE §11.2 and §4.15: this is read-only shared reference data that every tenant
/// reads identically, there is nothing for a tenant to "customize" the way it customizes
/// its own VatRate rows, so a tenant copy would just be a second place to keep in sync.
///
/// Source: European Commission TEDB (Taxes in Europe Database) /
/// https://taxation-customs.ec.europa.eu/taxation/vat/eu-vat-rules-topic/vat-rates_en
/// Rates recorded here were captured 2026-10-02 — see MasterDbContext seed comment for
/// the exact snapshot and citation per country.
/// </summary>
public class OssVatRate : BaseEntity
{
    /// <summary>
    /// ISO 3166-1 alpha-2 country code of the EU member state this rate applies to
    /// (e.g. "DE", "FR", "AT"). Always one of the 26 EU states other than CZ — Czech
    /// sales never go through OSS, they are domestic DPH.
    /// </summary>
    public string CountryCode { get; set; } = string.Empty;

    /// <summary>VAT rate as a percentage (e.g. 19.00 for Germany's standard rate).</summary>
    public decimal Rate { get; set; }

    /// <summary>Which rate bucket this is (standard/reduced/super-reduced/parking) — display only.</summary>
    public EOssVatRateCategory Category { get; set; }

    /// <summary>
    /// Free-text description of what this rate/category covers in that country
    /// (e.g. "Standardní sazba", "Potraviny, knihy"). Shown in the admin code-table UI.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>Date from which this rate is valid.</summary>
    public DateOnly ValidFrom { get; set; }

    /// <summary>Date until which this rate is valid (inclusive). Null = valid indefinitely.</summary>
    public DateOnly? ValidTo { get; set; }

    /// <summary>
    /// Whether this rate is currently offered for selection. Soft-disable instead of
    /// deleting — historical invoices must keep referencing the rate they used.
    /// </summary>
    public bool IsActive { get; set; } = true;
}
