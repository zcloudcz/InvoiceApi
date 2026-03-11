namespace Fakvio.Contracts.Dto.Tax;

/// <summary>
/// Result of a tax estimation calculation.
/// Contains the estimated income tax, social insurance, health insurance, and net income.
///
/// Junior note: All amounts are annual unless noted otherwise.
/// Monthly advances are provided separately for convenience.
/// </summary>
public class TaxEstimationResult
{
    // ─── Inputs (echoed back for display) ───────────────────────────────

    /// <summary>
    /// Gross annual income used for calculation.
    /// </summary>
    public decimal GrossIncome { get; set; }

    /// <summary>
    /// Total expenses (lump-sum or actual) deducted from gross income.
    /// </summary>
    public decimal Expenses { get; set; }

    /// <summary>
    /// Tax regime used for calculation.
    /// </summary>
    public string TaxRegime { get; set; } = string.Empty;

    /// <summary>
    /// Country and year of calculation.
    /// </summary>
    public string Country { get; set; } = string.Empty;
    public int Year { get; set; }

    /// <summary>
    /// Currency code for all amounts ("CZK" or "EUR").
    /// </summary>
    public string CurrencyCode { get; set; } = "CZK";

    // ─── Tax base ───────────────────────────────────────────────────────

    /// <summary>
    /// Tax base (základ daně) = GrossIncome - Expenses.
    /// This is the amount from which income tax and insurance are calculated.
    /// </summary>
    public decimal TaxBase { get; set; }

    // ─── Income tax ─────────────────────────────────────────────────────

    /// <summary>
    /// Annual income tax before credits.
    /// </summary>
    public decimal IncomeTaxBeforeCredits { get; set; }

    /// <summary>
    /// Tax credits applied (e.g., basic taxpayer credit).
    /// </summary>
    public decimal TaxCredits { get; set; }

    /// <summary>
    /// Annual income tax after credits (final amount to pay).
    /// </summary>
    public decimal IncomeTax { get; set; }

    // ─── Social insurance ───────────────────────────────────────────────

    /// <summary>
    /// Annual social insurance assessment base.
    /// </summary>
    public decimal SocialAssessmentBase { get; set; }

    /// <summary>
    /// Annual social insurance total.
    /// </summary>
    public decimal SocialInsurance { get; set; }

    /// <summary>
    /// Monthly social insurance advance payment.
    /// </summary>
    public decimal MonthlyAdvanceSocial { get; set; }

    // ─── Health insurance ───────────────────────────────────────────────

    /// <summary>
    /// Annual health insurance assessment base.
    /// </summary>
    public decimal HealthAssessmentBase { get; set; }

    /// <summary>
    /// Annual health insurance total.
    /// </summary>
    public decimal HealthInsurance { get; set; }

    /// <summary>
    /// Monthly health insurance advance payment.
    /// </summary>
    public decimal MonthlyAdvanceHealth { get; set; }

    // ─── Totals ─────────────────────────────────────────────────────────

    /// <summary>
    /// Total annual obligations (income tax + social + health).
    /// </summary>
    public decimal TotalObligations { get; set; }

    /// <summary>
    /// Total monthly obligations (for budgeting).
    /// </summary>
    public decimal TotalMonthlyObligations { get; set; }

    /// <summary>
    /// Net income after all deductions = GrossIncome - Expenses - TotalObligations.
    /// </summary>
    public decimal NetIncome { get; set; }

    /// <summary>
    /// Effective tax rate as percentage = TotalObligations / GrossIncome * 100.
    /// </summary>
    public decimal EffectiveTaxRate { get; set; }

    // ─── Step-by-step breakdown ─────────────────────────────────────────

    /// <summary>
    /// Detailed calculation steps for transparency.
    /// Each entry explains one step of the calculation (e.g., "Expense deduction: 60% of 1,000,000 = 600,000").
    /// </summary>
    public List<TaxCalculationStep> Steps { get; set; } = new();
}

/// <summary>
/// One step in the tax calculation — used to show the user how the result was computed.
/// </summary>
public class TaxCalculationStep
{
    /// <summary>
    /// Localization key for the step description (e.g., "Tax_Step_ExpenseDeduction").
    /// </summary>
    public string LabelKey { get; set; } = string.Empty;

    /// <summary>
    /// Fallback description in English (used when localization key is not found).
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// The calculated amount for this step.
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Formula or explanation (e.g., "1,000,000 x 60% = 600,000").
    /// </summary>
    public string? Formula { get; set; }
}
