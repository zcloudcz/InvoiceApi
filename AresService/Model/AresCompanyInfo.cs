namespace AresService.Model;

/// <summary>
/// Represents company information retrieved from ARES registry
/// Simplified model containing the most important data
/// </summary>
public class AresCompanyInfo
{
    /// <summary>
    /// Company registration number (IČO)
    /// Example: "12345678"
    /// </summary>
    public string RegistrationNumber { get; set; } = string.Empty;

    /// <summary>
    /// Official company name
    /// Example: "ABC Company s.r.o."
    /// </summary>
    public string CompanyName { get; set; } = string.Empty;

    /// <summary>
    /// Tax identification number (DIČ)
    /// Example: "CZ12345678"
    /// Null if company is not VAT registered
    /// </summary>
    public string? TaxNumber { get; set; }

    /// <summary>
    /// Is this company a VAT payer?
    /// </summary>
    public bool IsVatPayer { get; set; }

    /// <summary>
    /// Primary/registered office address
    /// </summary>
    public AresAddress? Address { get; set; }

    /// <summary>
    /// Legal form of the company
    /// Example: "s.r.o." (limited liability company), "a.s." (joint-stock company)
    /// </summary>
    public string? LegalForm { get; set; }

    /// <summary>
    /// When was this data retrieved from ARES
    /// </summary>
    public DateTime FetchedAt { get; set; }

    /// <summary>
    /// Was the lookup successful?
    /// False if company not found or API error occurred
    /// </summary>
    public bool IsSuccessful { get; set; }

    /// <summary>
    /// Error message if lookup failed
    /// Null if IsSuccessful = true
    /// </summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Address information from ARES
/// </summary>
public class AresAddress
{
    /// <summary>
    /// Street name and building number
    /// Example: "Hlavní 123"
    /// </summary>
    public string Street { get; set; } = string.Empty;

    /// <summary>
    /// City name
    /// Example: "Praha"
    /// </summary>
    public string City { get; set; } = string.Empty;

    /// <summary>
    /// Postal code
    /// Example: "120 00"
    /// </summary>
    public string PostalCode { get; set; } = string.Empty;

    /// <summary>
    /// Country
    /// Usually "Česká republika" for Czech companies
    /// </summary>
    public string Country { get; set; } = "Česká republika";

    /// <summary>
    /// District or region
    /// Example: "Praha 2"
    /// </summary>
    public string? District { get; set; }
}
