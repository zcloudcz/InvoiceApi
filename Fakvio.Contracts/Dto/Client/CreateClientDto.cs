using System.ComponentModel.DataAnnotations;

namespace Fakvio.Contracts.Dto.Client;

/// <summary>
/// DTO for creating a new client
/// Contains only fields needed for client creation
/// </summary>
public class CreateClientDto
{
    /// <summary>
    /// Company registration number (IČO)
    /// Optional — physical persons (end customers) may not have one.
    /// When provided, must be 8-20 characters.
    /// </summary>
    [StringLength(20, ErrorMessage = "Registration number must be at most 20 characters")]
    public string? RegistrationNumber { get; set; }

    /// <summary>
    /// Tax identification number (DIČ)
    /// Optional - only for VAT payers
    /// </summary>
    [StringLength(50)]
    public string? TaxNumber { get; set; }

    /// <summary>
    /// Official company name
    /// </summary>
    [Required(ErrorMessage = "Company name is required")]
    [StringLength(500, MinimumLength = 1, ErrorMessage = "Company name must be 1-500 characters")]
    public string CompanyName { get; set; } = string.Empty;

    /// <summary>
    /// Trading name if different from official name
    /// </summary>
    [StringLength(500)]
    public string? TradingName { get; set; }

    /// <summary>
    /// Is this company a VAT payer?
    /// </summary>
    public bool IsVatPayer { get; set; }

    /// <summary>
    /// Is this client the issuer (your company)?
    /// True = company issuing invoices, False = customer receiving invoices
    /// </summary>
    public bool IsIssuer { get; set; } = false;

    /// <summary>
    /// Preferred language for documents (PDFs, emails) — ISO 639-1 code.
    /// Defaults to "cs" (Czech). Max 5 characters (e.g., "cs", "en", "de").
    /// </summary>
    [StringLength(5)]
    public string Language { get; set; } = "cs";

    /// <summary>
    /// Should this client be fetched from ARES automatically?
    /// If true, system will try to fetch data from ARES by registration number
    /// </summary>
    public bool FetchFromAres { get; set; } = true;

    /// <summary>
    /// Collection of addresses
    /// At least one address is recommended
    /// </summary>
    public List<CreateAddressDto> Address { get; set; } = new();

    /// <summary>
    /// Collection of contacts
    /// At least one contact (email or phone) is recommended
    /// </summary>
    public List<CreateContactDto> Contact { get; set; } = new();

    /// <summary>
    /// Collection of bank accounts.
    /// At least one is recommended for issuers (shown on invoices).
    /// The first account added automatically becomes the default.
    /// </summary>
    public List<CreateBankAccountDto> BankAccount { get; set; } = new();

    /// <summary>
    /// Billing settings for this client
    /// Optional - if not provided, default settings will be used
    /// </summary>
    public CreateBillingSettingsDto? BillingSettings { get; set; }
}
