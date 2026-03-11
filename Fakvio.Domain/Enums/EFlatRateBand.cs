namespace Fakvio.Domain.Enums;

/// <summary>
/// CZ Paušální daň band (pásmo) — determines the fixed monthly payment amount.
/// Each band has different income limits depending on activity type.
///
/// Localization key pattern: EFlatRateBand_{Value} (e.g., EFlatRateBand_Band1).
/// </summary>
public enum EFlatRateBand
{
    /// <summary>
    /// Pásmo 1 (nejnižší) — lowest monthly payment.
    /// Income limit: 1M CZK (all), 1.5M (60%/80% activities), 2M (80% activities).
    /// 2026: 9,984 CZK/month.
    /// </summary>
    Band1 = 1,

    /// <summary>
    /// Pásmo 2 (střední) — middle monthly payment.
    /// Income limit: 1.5M CZK (all), 2M (60%/80% activities).
    /// 2026: 16,745 CZK/month.
    /// </summary>
    Band2 = 2,

    /// <summary>
    /// Pásmo 3 (nejvyšší) — highest monthly payment.
    /// Income limit: 2M CZK (all remaining).
    /// 2026: 27,139 CZK/month.
    /// </summary>
    Band3 = 3
}
