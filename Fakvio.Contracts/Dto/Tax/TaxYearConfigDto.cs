namespace Fakvio.Contracts.Dto.Tax;

/// <summary>
/// DTO for TaxYearConfig — annual tax rates and thresholds for a specific country and year.
/// Used to display and manage tax configuration in the admin UI.
/// </summary>
public class TaxYearConfigDto
{
    public long Id { get; set; }
    public int Year { get; set; }
    public string Country { get; set; } = "CZ";
    public string CurrencyCode { get; set; } = "CZK";

    // Common
    public decimal AverageMonthlyWage { get; set; }
    public decimal LivingMinimum { get; set; }

    // Income tax
    public decimal IncomeTaxRate { get; set; }
    public decimal ProgressiveTaxRate { get; set; }
    public decimal ProgressiveThreshold { get; set; }
    public decimal BasicTaxpayerCredit { get; set; }

    // Social insurance
    public decimal SocialInsuranceRate { get; set; }
    public decimal SocialAssessmentBasePercent { get; set; }
    public decimal MinMonthlySocialMain { get; set; }
    public decimal MinMonthlySocialSecondary { get; set; }
    public decimal MaxSocialAssessmentBase { get; set; }

    // Health insurance
    public decimal HealthInsuranceRate { get; set; }
    public decimal HealthAssessmentBasePercent { get; set; }
    public decimal MinMonthlyHealthMain { get; set; }
    public decimal MaxHealthAssessmentBase { get; set; }

    // Flat-rate tax (CZ)
    public decimal FlatRateBand1Monthly { get; set; }
    public decimal FlatRateBand2Monthly { get; set; }
    public decimal FlatRateBand3Monthly { get; set; }

    // Lump-sum caps
    public decimal LumpSum80Cap { get; set; }
    public decimal LumpSum60Cap { get; set; }
    public decimal LumpSum40Cap { get; set; }
    public decimal LumpSum30Cap { get; set; }
}
