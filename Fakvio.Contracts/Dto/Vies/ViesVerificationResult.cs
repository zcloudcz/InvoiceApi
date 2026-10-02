namespace Fakvio.Contracts.Dto.Vies;

/// <summary>
/// Outcome of a VIES (VAT Information Exchange System) lookup.
/// Deliberately separates "the VAT ID is invalid" from "VIES could not be reached" —
/// a timeout or a member state outage must never be presented to the user as "invalid DIČ".
/// </summary>
public enum EViesCheckStatus
{
    /// <summary>The VAT ID is registered and active.</summary>
    Valid = 0,

    /// <summary>VIES answered and the VAT ID is not valid (or the input was malformed).</summary>
    Invalid = 1,

    /// <summary>VIES (or the member state behind it) could not complete the check — retry later.</summary>
    Unavailable = 2
}

/// <summary>
/// Result of a <c>GET /api/vies/{vatId}</c> lookup.
/// </summary>
public class ViesVerificationResult
{
    public EViesCheckStatus Status { get; set; }

    /// <summary>Convenience flag — true only when <see cref="Status"/> is <see cref="EViesCheckStatus.Valid"/>.</summary>
    public bool Valid => Status == EViesCheckStatus.Valid;

    /// <summary>Two-letter country code as sent to VIES (e.g. "CZ"; Greece normalized to "EL").</summary>
    public string CountryCode { get; set; } = string.Empty;

    /// <summary>VAT number without the country prefix.</summary>
    public string VatNumber { get; set; } = string.Empty;

    /// <summary>Registered company name, when the member state releases it. Null when unavailable.</summary>
    public string? Name { get; set; }

    /// <summary>Registered address, when the member state releases it. Null when unavailable.</summary>
    public string? Address { get; set; }

    /// <summary>Timestamp VIES reported for the check, when available.</summary>
    public DateTime? RequestDate { get; set; }

    /// <summary>Human-readable reason when <see cref="Status"/> is not <see cref="EViesCheckStatus.Valid"/>.</summary>
    public string? ErrorMessage { get; set; }
}
