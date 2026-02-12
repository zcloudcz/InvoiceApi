using InvoiceApi.Domain.Common;

namespace InvoiceApi.Domain.Entities;

/// <summary>
/// Cache for ARES (business registry) lookups
/// Stores fetched company data to avoid repeated API calls
/// Data expires after 30 days
/// </summary>
public class AresCache : BaseEntity
{
    /// <summary>
    /// Company registration number (IČO)
    /// This is the lookup key - unique identifier in business registry
    /// Example: "12345678"
    /// </summary>
    public string RegistrationNumber { get; set; } = string.Empty;

    /// <summary>
    /// Complete JSON response from ARES API
    /// Stored as-is for future reference and re-parsing if needed
    /// Contains all available data about the company
    /// </summary>
    public string JsonData { get; set; } = string.Empty;

    /// <summary>
    /// When was this data fetched from ARES
    /// Used to determine if cache is still valid (< 30 days old)
    /// </summary>
    public DateTime FetchedAt { get; set; }

    /// <summary>
    /// When does this cache entry expire
    /// Calculated as FetchedAt + 30 days
    /// After expiration, new ARES lookup should be performed
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Was the ARES lookup successful?
    /// True = Valid company found in registry
    /// False = Company not found or API error
    /// We cache both successful and failed lookups to avoid repeated failed attempts
    /// </summary>
    public bool IsSuccessful { get; set; }

    /// <summary>
    /// Error message if lookup failed
    /// Null if IsSuccessful = true
    /// Example: "Company not found", "ARES API unavailable"
    /// </summary>
    public string? ErrorMessage { get; set; }

    // Extracted key fields for quick access (duplicated from JsonData for performance)

    /// <summary>
    /// Company name from ARES
    /// Extracted from JSON for quick access without parsing
    /// </summary>
    public string? CompanyName { get; set; }

    /// <summary>
    /// Tax number (DIČ) from ARES
    /// Null if company is not VAT registered
    /// </summary>
    public string? TaxNumber { get; set; }

    /// <summary>
    /// Is this company a VAT payer
    /// Important for invoice generation
    /// </summary>
    public bool? IsVatPayer { get; set; }
}
