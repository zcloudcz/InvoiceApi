namespace Fakvio.Contracts.Dto.Client;

/// <summary>
/// DTO for client data
/// Used for API responses when returning client information
/// </summary>
public class ClientDto
{
    /// <summary>
    /// Client ID
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Company registration number (IČO)
    /// Nullable — physical persons (end customers) may not have one.
    /// </summary>
    public string? RegistrationNumber { get; set; }

    /// <summary>
    /// Tax identification number (DIČ)
    /// </summary>
    public string? TaxNumber { get; set; }

    /// <summary>
    /// Official company name
    /// </summary>
    public string CompanyName { get; set; } = string.Empty;

    /// <summary>
    /// Trading name if different from official name
    /// </summary>
    public string? TradingName { get; set; }

    /// <summary>
    /// Is this company a VAT payer?
    /// </summary>
    public bool IsVatPayer { get; set; }

    /// <summary>
    /// Is this the issuer (your company)?
    /// </summary>
    public bool IsIssuer { get; set; }

    /// <summary>
    /// Is this client active?
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// When was data last fetched from ARES
    /// </summary>
    public DateTime? LastAresFetchDate { get; set; }

    /// <summary>
    /// Preferred language for documents (PDFs, emails) — ISO 639-1 code (e.g., "cs", "en").
    /// </summary>
    public string Language { get; set; } = "cs";

    /// <summary>
    /// Display color for this client in grids (CSS hex, e.g., "#FF5722"). Null = default.
    /// </summary>
    public string? Color { get; set; }

    /// <summary>
    /// Manual override of the Peppol network endpoint ID for the UBL eInvoice export
    /// (ADR 0002, F1.8), e.g. "0245:2020123456". Null = derived automatically from
    /// <see cref="TaxNumber"/> and the primary address' country.
    /// </summary>
    public string? PeppolId { get; set; }

    /// <summary>
    /// Collection of client addresses
    /// </summary>
    public List<AddressDto> Address { get; set; } = new();

    /// <summary>
    /// Collection of client contacts
    /// </summary>
    public List<ContactDto> Contact { get; set; } = new();

    /// <summary>
    /// Collection of client bank accounts (1:N).
    /// Issuers use these as payment destinations on invoices.
    /// </summary>
    public List<BankAccountDto> BankAccount { get; set; } = new();

    /// <summary>
    /// Billing settings for this client
    /// </summary>
    public BillingSettingsDto? BillingSettings { get; set; }

    /// <summary>
    /// Tax regime used by this company (e.g., FlatRateTax, LumpSumExpenses60).
    /// </summary>
    public string? TaxRegime { get; set; }

    /// <summary>
    /// Type of business activity — determines lump-sum expense percentage (CZ).
    /// </summary>
    public string? ActivityType { get; set; }

    /// <summary>
    /// Whether this is the person's main self-employed activity.
    /// Main activity has higher minimum insurance advances.
    /// </summary>
    public bool IsMainActivity { get; set; } = true;

    /// <summary>
    /// Flat-rate tax band (CZ only, 1/2/3) — for Paušální daň regime.
    /// </summary>
    public string? FlatRateBand { get; set; }

    /// <summary>
    /// Issuer setting: auto-issue a tax receipt for advance payment (DPP) when a proforma is paid.
    /// Applies to VAT-payer issuers only.
    /// </summary>
    public bool AutoIssueTaxReceiptForAdvance { get; set; } = true;

    /// <summary>
    /// When was this record created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When was this record last updated
    /// </summary>
    public DateTime? UpdatedAt { get; set; }
}
