namespace Fakvio.Domain.Enums;

/// <summary>
/// Defines the tax/accounting regime used by a self-employed person (OSVČ/SZČO).
/// Determines how income tax, social insurance, and health insurance are calculated.
///
/// CZ = Czech Republic, SK = Slovakia.
/// Localization key pattern: ETaxRegime_{Value} (e.g., ETaxRegime_FlatRateTax).
/// </summary>
public enum ETaxRegime
{
    /// <summary>
    /// CZ only: Paušální daň — single fixed monthly payment covering income tax + social + health.
    /// No annual tax return required. Only for non-VAT payers with income up to 2M CZK.
    /// </summary>
    FlatRateTax = 1,

    /// <summary>
    /// CZ: Výdajový paušál 80% — for craft trades (řemeslné živnosti) and agriculture.
    /// Max deductible expenses: 1,600,000 CZK.
    /// </summary>
    LumpSumExpenses80 = 2,

    /// <summary>
    /// CZ: Výdajový paušál 60% — for non-craft trades (volné živnosti), IT, consulting.
    /// SK: Paušálne výdavky 60% — max 20,000 EUR.
    /// CZ max deductible expenses: 1,200,000 CZK.
    /// </summary>
    LumpSumExpenses60 = 3,

    /// <summary>
    /// CZ only: Výdajový paušál 40% — for regulated professions (lawyers, architects, doctors).
    /// Max deductible expenses: 800,000 CZK.
    /// </summary>
    LumpSumExpenses40 = 4,

    /// <summary>
    /// CZ only: Výdajový paušál 30% — for rental income (příjmy z nájmu).
    /// Max deductible expenses: 600,000 CZK.
    /// </summary>
    LumpSumExpenses30 = 5,

    /// <summary>
    /// CZ: Daňová evidence — cash-based simple bookkeeping with actual expenses.
    /// SK: Jednoduché účtovníctvo — single-entry bookkeeping.
    /// </summary>
    TaxRecords = 6,

    /// <summary>
    /// CZ: Účetnictví (podvojné) — full double-entry accrual-based accounting.
    /// SK: Podvojné účtovníctvo — same concept.
    /// Mandatory when annual turnover exceeds 25M CZK (CZ).
    /// </summary>
    FullAccounting = 7
}
