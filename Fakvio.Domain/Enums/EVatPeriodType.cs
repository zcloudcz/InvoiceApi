namespace Fakvio.Domain.Enums;

/// <summary>
/// Specifies whether a VAT reporting period is monthly or quarterly.
/// Used in EPO DPHDP3 export to determine which XSD attribute to populate
/// (VetaD/@mesic for monthly, VetaD/@ctvrt for quarterly) and to validate
/// the period number range (1-12 vs 1-4).
/// </summary>
public enum EVatPeriodType
{
    /// <summary>
    /// Monthly period — period number 1 to 12 (January to December).
    /// Most VAT payers whose annual turnover exceeds the quarterly threshold use monthly periods.
    /// Populates VetaD/@mesic in the generated DPHDP3 XML.
    /// </summary>
    Monthly = 1,

    /// <summary>
    /// Quarterly period — period number 1 to 4 (Q1 to Q4).
    /// Available to VAT payers with lower annual turnover (§ 99a ZDPH).
    /// Populates VetaD/@ctvrt in the generated DPHDP3 XML.
    /// </summary>
    Quarterly = 2
}
