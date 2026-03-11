using System.ComponentModel.DataAnnotations;

namespace Fakvio.Contracts.Dto.Client;

/// <summary>
/// DTO for updating an existing client
/// All fields are optional - only provided fields will be updated
/// </summary>
public class UpdateClientDto
{
    /// <summary>
    /// Tax identification number (DIČ)
    /// </summary>
    [StringLength(50)]
    public string? TaxNumber { get; set; }

    /// <summary>
    /// Official company name
    /// </summary>
    [StringLength(500, MinimumLength = 1)]
    public string? CompanyName { get; set; }

    /// <summary>
    /// Trading name if different from official name
    /// </summary>
    [StringLength(500)]
    public string? TradingName { get; set; }

    /// <summary>
    /// Is this company a VAT payer?
    /// </summary>
    public bool? IsVatPayer { get; set; }

    /// <summary>
    /// Is this client the issuer (your company)?
    /// </summary>
    public bool? IsIssuer { get; set; }

    /// <summary>
    /// Is this client active?
    /// </summary>
    public bool? IsActive { get; set; }

    /// <summary>
    /// Preferred language for documents — ISO 639-1 code (e.g., "cs", "en").
    /// Null means "don't change".
    /// </summary>
    [StringLength(5)]
    public string? Language { get; set; }

    /// <summary>
    /// Tax regime used by this company. Null = don't change.
    /// </summary>
    public string? TaxRegime { get; set; }

    /// <summary>
    /// Type of business activity. Null = don't change.
    /// </summary>
    public string? ActivityType { get; set; }

    /// <summary>
    /// Whether this is the person's main self-employed activity.
    /// </summary>
    public bool? IsMainActivity { get; set; }

    /// <summary>
    /// Flat-rate tax band (CZ only). Null = don't change.
    /// </summary>
    public string? FlatRateBand { get; set; }

    /// <summary>
    /// Should data be refreshed from ARES?
    /// </summary>
    public bool RefreshFromAres { get; set; } = false;

    /// <summary>
    /// Updated addresses. If provided, replaces all existing addresses for the client.
    /// </summary>
    public List<UpdateAddressDto>? Address { get; set; }

    /// <summary>
    /// Updated contacts. If provided, replaces all existing contacts for the client.
    /// </summary>
    public List<UpdateContactDto>? Contact { get; set; }

    /// <summary>
    /// Updated bank accounts. If provided, replaces all existing bank accounts for the client.
    /// Uses the same replace-all strategy as Address and Contact.
    /// </summary>
    public List<UpdateBankAccountDto>? BankAccount { get; set; }

    /// <summary>
    /// Updated billing settings. If provided, updates the client's billing settings.
    /// </summary>
    public UpdateBillingSettingsDto? BillingSettings { get; set; }
}
