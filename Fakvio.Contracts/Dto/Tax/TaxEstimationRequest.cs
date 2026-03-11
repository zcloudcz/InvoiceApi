namespace Fakvio.Contracts.Dto.Tax;

/// <summary>
/// Request DTO for tax estimation calculation.
/// Contains the inputs needed to estimate income tax, social insurance, and health insurance.
///
/// Junior note: The user provides their gross income and tax regime,
/// and the service calculates all obligations based on the TaxYearConfig rates.
/// </summary>
public class TaxEstimationRequest
{
    /// <summary>
    /// Gross annual income (příjmy) — total revenue before any deductions.
    /// Typically calculated from issued invoices in the given year.
    /// </summary>
    public decimal GrossIncome { get; set; }

    /// <summary>
    /// Actual annual expenses (only needed for TaxRecords and FullAccounting regimes).
    /// For lump-sum regimes, expenses are calculated automatically from the percentage.
    /// </summary>
    public decimal? ActualExpenses { get; set; }

    /// <summary>
    /// Tax year to calculate for (e.g., 2026).
    /// </summary>
    public int Year { get; set; }

    /// <summary>
    /// Country code: "CZ" or "SK".
    /// </summary>
    public string Country { get; set; } = "CZ";

    /// <summary>
    /// Tax regime to use for calculation. See ETaxRegime enum.
    /// Stored as string for API compatibility (parsed to enum on the backend).
    /// </summary>
    public string TaxRegime { get; set; } = string.Empty;

    /// <summary>
    /// Activity type (CZ only) — determines lump-sum expense percentage.
    /// </summary>
    public string? ActivityType { get; set; }

    /// <summary>
    /// Whether this is the person's main self-employed activity.
    /// Main activity has higher minimum insurance advances.
    /// </summary>
    public bool IsMainActivity { get; set; } = true;
}
