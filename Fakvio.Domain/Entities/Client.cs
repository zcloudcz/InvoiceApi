using Fakvio.Domain.Common;
using Fakvio.Domain.Enums;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Represents a client (customer) or the issuer (your company)
/// Both are stored in the same table, distinguished by IsIssuer flag
/// Contains basic company information from business registry (ARES)
/// </summary>
public class Client : BaseEntity
{
    /// <summary>
    /// Company registration number (IČO in Czech Republic)
    /// Unique identifier from business registry
    /// Example: "12345678"
    /// Nullable — physical persons (end customers) may not have a registration number.
    /// </summary>
    public string? RegistrationNumber { get; set; }

    /// <summary>
    /// Tax identification number (DIČ in Czech Republic)
    /// Only for VAT payers
    /// Example: "CZ12345678"
    /// Can be null if company is not VAT registered
    /// </summary>
    public string? TaxNumber { get; set; }

    /// <summary>
    /// Official company name from business registry
    /// Example: "ABC Company s.r.o."
    /// </summary>
    public string CompanyName { get; set; } = string.Empty;

    /// <summary>
    /// Trading name if different from official name
    /// Example: Official name "ABC Trading s.r.o." but trades as "ABC Shop"
    /// Can be null if same as CompanyName
    /// </summary>
    public string? TradingName { get; set; }

    /// <summary>
    /// Is this company a VAT payer?
    /// Important for invoice generation - determines if VAT should be shown
    /// If false, invoice is issued "without VAT" (neplátce DPH)
    /// </summary>
    public bool IsVatPayer { get; set; }

    /// <summary>
    /// Is this client actually the issuer (your company)?
    /// True = This is your company issuing invoices
    /// False = This is a customer receiving invoices
    /// There should typically be one issuer per tenant/database
    /// </summary>
    public bool IsIssuer { get; set; } = false;

    /// <summary>
    /// When was this client data last fetched from ARES
    /// Used to determine if data needs refresh
    /// Null = never fetched or manually created
    /// </summary>
    public DateTime? LastAresFetchDate { get; set; }

    /// <summary>
    /// Is this client active and available for creating invoices
    /// Inactive clients are hidden from selection but data is preserved
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Preferred language for documents (PDFs, emails) generated for this client.
    /// ISO 639-1 code: "cs" = Czech, "en" = English.
    /// Used to select the correct ContentTemplate when generating invoices/emails.
    /// Default is "cs" (Czech) — the primary audience of this application.
    /// </summary>
    public string Language { get; set; } = "cs";

    // ─── Tax / Accounting regime ──────────────────────────────────────────

    /// <summary>
    /// Tax/accounting regime used by this company (OSVČ/SZČO).
    /// Determines how income tax, social and health insurance are calculated.
    /// Null = not configured (not applicable or unknown).
    /// Only relevant for issuers (IsIssuer = true).
    /// </summary>
    public ETaxRegime? TaxRegime { get; set; }

    /// <summary>
    /// Type of self-employed activity — determines lump-sum expense percentage (CZ).
    /// Null = not configured.
    /// </summary>
    public EActivityType? ActivityType { get; set; }

    /// <summary>
    /// Whether this is the person's main (primary) self-employed activity.
    /// Main activity has higher minimum social/health insurance advances.
    /// True = hlavní činnost, False = vedlejší činnost.
    /// </summary>
    public bool IsMainActivity { get; set; } = true;

    /// <summary>
    /// Flat-rate tax band (CZ only). Only applicable when TaxRegime = FlatRateTax.
    /// Determines the fixed monthly payment amount.
    /// </summary>
    public EFlatRateBand? FlatRateBand { get; set; }

    /// <summary>
    /// Whether a Tax Receipt for Advance Payment (DPP, daňový doklad o přijaté platbě) is
    /// issued automatically when a Proforma of this issuer receives a payment.
    /// Only relevant for issuers (IsIssuer = true). Non-VAT-payer issuers never issue a DPP
    /// regardless of this flag — an advance payment only creates a VAT obligation for VAT payers.
    /// When false, the DPP must be issued manually from the proforma detail page (or the
    /// issue_tax_receipt MCP/chat tool).
    /// Default: true.
    /// </summary>
    public bool AutoIssueTaxReceiptForAdvance { get; set; } = true;

    /// <summary>
    /// Display color for this client in grids and UI elements.
    /// CSS hex color code (e.g., "#FF5722", "#4CAF50").
    /// Null = no custom color (default row styling).
    /// </summary>
    public string? Color { get; set; }

    /// <summary>
    /// Preferred currency for this client
    /// Used as default when creating invoices for this client
    /// If null, system default currency (CZK) is used
    /// </summary>
    public long? PreferredCurrencyId { get; set; }

    /// <summary>
    /// Navigation property to preferred currency
    /// </summary>
    public Currency? PreferredCurrency { get; set; }

    // Navigation properties - related data stored in separate tables

    /// <summary>
    /// Collection of all addresses for this client
    /// Can have multiple addresses: primary, billing, shipping, etc.
    /// </summary>
    public ICollection<Address> Address { get; set; } = new List<Address>();

    /// <summary>
    /// Collection of contact information
    /// Can have multiple contacts: emails, phones, etc.
    /// </summary>
    public ICollection<Contact> Contact { get; set; } = new List<Contact>();

    /// <summary>
    /// Collection of bank accounts for this client.
    /// Can have multiple accounts (CZK, EUR, etc.) with one marked as default.
    /// Follows the same 1:N pattern as Address and Contact.
    /// </summary>
    public ICollection<BankAccount> BankAccount { get; set; } = new List<BankAccount>();

    /// <summary>
    /// Billing settings specific to this client
    /// Contains payment terms, number sequences, etc.
    /// Can be null if using default settings
    /// </summary>
    public BillingSettings? BillingSettings { get; set; }

    // ─── EPO / Tax authority ──────────────────────────────────────────────

    /// <summary>
    /// Czech Financial Administration tax office code (c_ufo) — číselník Územní finanční orgány.
    /// Required for EPO DPHDP3 / DPHKH1 submissions; only relevant when IsIssuer = true.
    /// Null = not yet configured by the user; EPO export will raise EpoValidationException
    /// until the code is set in Company Settings.
    /// Valid range: 1–999 (3-digit code per MFČR číselník UFO).
    /// Example: 451 = Finanční úřad pro hl. m. Prahu.
    /// </summary>
    public int? EpoTaxOfficeCode { get; set; }

    /// <summary>
    /// Manual override of the Peppol network endpoint ID (BT-34/BT-49) used by the UBL/Peppol
    /// eInvoice export (ADR 0002, F1.8). Format <c>"&lt;scheme&gt;:&lt;value&gt;"</c>, e.g.
    /// <c>"0245:2020123456"</c> (SK) or <c>"9929:CZ12345678"</c> (CZ) — same shape
    /// <see cref="Fakvio.Infrastructure.Service.Ubl.UblCodes.EndpointId"/> derives automatically
    /// from <see cref="TaxNumber"/> and the primary address' country.
    /// Null (the default) = no override, use the automatic derivation. Only needed when that
    /// derivation is wrong or insufficient — a VAT group, a foreign client outside CZ/SK, or an
    /// id that does not follow the usual national DIČ pattern.
    /// </summary>
    public string? PeppolId { get; set; }
}
