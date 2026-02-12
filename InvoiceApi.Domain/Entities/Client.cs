using InvoiceApi.Domain.Common;

namespace InvoiceApi.Domain.Entities;

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
    /// Billing settings specific to this client
    /// Contains payment terms, number sequences, etc.
    /// Can be null if using default settings
    /// </summary>
    public BillingSettings? BillingSettings { get; set; }
}
