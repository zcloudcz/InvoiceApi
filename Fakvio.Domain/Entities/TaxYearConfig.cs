using Fakvio.Domain.Common;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Stores annual tax rates and thresholds for CZ and SK.
/// Rates change every year — this entity allows configuring them without code changes.
///
/// One record per (Year, Country) combination. Seed data provided for 2025 and 2026.
/// Used by TaxEstimationService to calculate income tax, social/health insurance obligations.
///
/// Junior note: All monetary amounts are in the local currency (CZK for CZ, EUR for SK).
/// Percentages are stored as decimals (e.g., 15.0 = 15%, 29.2 = 29.2%).
/// </summary>
public class TaxYearConfig : BaseEntity
{
    // ─── Identification ─────────────────────────────────────────────────

    /// <summary>
    /// Tax year (e.g., 2025, 2026). Together with Country forms a unique key.
    /// </summary>
    public int Year { get; set; }

    /// <summary>
    /// Country code: "CZ" (Czech Republic) or "SK" (Slovakia).
    /// </summary>
    public string Country { get; set; } = "CZ";

    // ─── Common rates ───────────────────────────────────────────────────

    /// <summary>
    /// Average monthly wage used for minimum/maximum assessment base calculation.
    /// CZ: průměrná mzda. SK: priemerná mesačná mzda.
    /// </summary>
    public decimal AverageMonthlyWage { get; set; }

    /// <summary>
    /// SK only: Životné minimum — used for non-taxable portion calculation.
    /// CZ: not used (set to 0).
    /// </summary>
    public decimal LivingMinimum { get; set; }

    // ─── Income tax ─────────────────────────────────────────────────────

    /// <summary>
    /// Standard income tax rate as percentage (e.g., 15.0 = 15%).
    /// CZ: 15%. SK: 15% (for income up to 100k EUR) or 19%.
    /// </summary>
    public decimal IncomeTaxRate { get; set; }

    /// <summary>
    /// Progressive (solidarity) tax rate as percentage (e.g., 23.0 = 23%).
    /// CZ: 23% above threshold. SK: 25/30/35% in progressive brackets.
    /// </summary>
    public decimal ProgressiveTaxRate { get; set; }

    /// <summary>
    /// Annual income threshold where progressive rate kicks in.
    /// CZ: 36x average wage. SK: 100,000 EUR (simplified).
    /// </summary>
    public decimal ProgressiveThreshold { get; set; }

    /// <summary>
    /// Annual basic taxpayer credit (sleva na poplatníka).
    /// CZ: 30,840 CZK (2026). SK: nezdaniteľná časť (5,966.73 EUR).
    /// </summary>
    public decimal BasicTaxpayerCredit { get; set; }

    // ─── Social insurance ───────────────────────────────────────────────

    /// <summary>
    /// Social insurance rate as percentage of assessment base.
    /// CZ: 29.2%. SK: 33.15%.
    /// </summary>
    public decimal SocialInsuranceRate { get; set; }

    /// <summary>
    /// Percentage of tax base used as social insurance assessment base.
    /// CZ: 55%. SK: ~50% (varies by coefficient).
    /// </summary>
    public decimal SocialAssessmentBasePercent { get; set; }

    /// <summary>
    /// Minimum monthly social insurance advance for main activity.
    /// CZ: 5,720 CZK (2026). SK: 303.11 EUR (2026).
    /// </summary>
    public decimal MinMonthlySocialMain { get; set; }

    /// <summary>
    /// Minimum monthly social insurance advance for secondary activity.
    /// CZ: 1,574 CZK (2026). SK: same as main (from 2026).
    /// </summary>
    public decimal MinMonthlySocialSecondary { get; set; }

    /// <summary>
    /// Maximum annual social insurance assessment base.
    /// CZ: 48x average wage = 2,350,416 CZK (2026). SK: 9,128 EUR/month * 12.
    /// </summary>
    public decimal MaxSocialAssessmentBase { get; set; }

    // ─── Health insurance ───────────────────────────────────────────────

    /// <summary>
    /// Health insurance rate as percentage of assessment base.
    /// CZ: 13.5%. SK: 16% (2026, was 15% in 2025).
    /// </summary>
    public decimal HealthInsuranceRate { get; set; }

    /// <summary>
    /// Percentage of tax base used as health insurance assessment base.
    /// CZ: 50%. SK: 50%.
    /// </summary>
    public decimal HealthAssessmentBasePercent { get; set; }

    /// <summary>
    /// Minimum monthly health insurance advance for main activity.
    /// CZ: 3,306 CZK (2026). SK: 121.92 EUR (2026).
    /// </summary>
    public decimal MinMonthlyHealthMain { get; set; }

    /// <summary>
    /// Maximum annual health insurance assessment base (0 = no cap).
    /// CZ: 0 (no cap). SK: 0 (no cap since recent years).
    /// </summary>
    public decimal MaxHealthAssessmentBase { get; set; }

    // ─── Flat-rate tax (CZ only) ────────────────────────────────────────

    /// <summary>
    /// CZ: Monthly payment for flat-rate tax band 1. SK: 0.
    /// 2026: 9,984 CZK.
    /// </summary>
    public decimal FlatRateBand1Monthly { get; set; }

    /// <summary>
    /// CZ: Monthly payment for flat-rate tax band 2. SK: 0.
    /// 2026: 16,745 CZK.
    /// </summary>
    public decimal FlatRateBand2Monthly { get; set; }

    /// <summary>
    /// CZ: Monthly payment for flat-rate tax band 3. SK: 0.
    /// 2026: 27,139 CZK.
    /// </summary>
    public decimal FlatRateBand3Monthly { get; set; }

    // ─── Lump-sum expense caps ──────────────────────────────────────────

    /// <summary>
    /// Maximum annual lump-sum expenses for 80% category (CZ only).
    /// 2026: 1,600,000 CZK.
    /// </summary>
    public decimal LumpSum80Cap { get; set; }

    /// <summary>
    /// Maximum annual lump-sum expenses for 60% category.
    /// CZ: 1,200,000 CZK. SK: 20,000 EUR.
    /// </summary>
    public decimal LumpSum60Cap { get; set; }

    /// <summary>
    /// Maximum annual lump-sum expenses for 40% category (CZ only).
    /// 2026: 800,000 CZK.
    /// </summary>
    public decimal LumpSum40Cap { get; set; }

    /// <summary>
    /// Maximum annual lump-sum expenses for 30% category (CZ only).
    /// 2026: 600,000 CZK.
    /// </summary>
    public decimal LumpSum30Cap { get; set; }

    /// <summary>
    /// Currency code for all monetary amounts in this config ("CZK" or "EUR").
    /// </summary>
    public string CurrencyCode { get; set; } = "CZK";
}
