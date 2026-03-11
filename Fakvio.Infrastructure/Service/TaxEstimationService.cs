using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Tax;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Calculates estimated tax obligations for self-employed persons (OSVČ/SZČO).
///
/// Supports Czech Republic (CZ) and Slovakia (SK) tax regimes:
/// - CZ: Paušální daň, výdajové paušály (80/60/40/30%), daňová evidence, účetnictví
/// - SK: Paušálne výdavky (60%), jednoduché/podvojné účtovníctvo
///
/// All rates are loaded from TaxYearConfig — no hard-coded rates.
///
/// Junior note: The calculation follows these steps:
/// 1. Determine expenses (lump-sum percentage or actual)
/// 2. Calculate tax base = gross income - expenses
/// 3. Calculate income tax (with progressive rate if applicable)
/// 4. Apply taxpayer credit
/// 5. Calculate social insurance (assessment base × rate, enforce min/max)
/// 6. Calculate health insurance (assessment base × rate, enforce min/max)
/// 7. Sum everything up and compute net income
/// </summary>
public class TaxEstimationService : ITaxEstimationService
{
    private readonly MasterDbContext _masterContext;
    private readonly TenantDbContext _tenantContext;
    private readonly ILogger<TaxEstimationService> _logger;

    public TaxEstimationService(
        MasterDbContext masterContext,
        TenantDbContext tenantContext,
        ILogger<TaxEstimationService> logger)
    {
        _masterContext = masterContext;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    /// <summary>
    /// Main estimation method — calculates all obligations for a single tax regime.
    /// </summary>
    public async Task<TaxEstimationResult> EstimateAsync(
        TaxEstimationRequest request, CancellationToken ct = default)
    {
        // Load tax configuration for the requested country and year.
        var config = await _masterContext.Set<TaxYearConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Country == request.Country && c.Year == request.Year, ct);

        if (config == null)
        {
            throw new InvalidOperationException(
                $"Tax configuration not found for {request.Country}/{request.Year}. " +
                "Please add a TaxYearConfig record for this combination.");
        }

        // Parse the regime enum from the request string.
        if (!Enum.TryParse<ETaxRegime>(request.TaxRegime, out var regime))
        {
            throw new ArgumentException($"Invalid tax regime: '{request.TaxRegime}'.");
        }

        _logger.LogInformation(
            "Calculating tax estimation: {Country}/{Year}, regime={Regime}, income={Income}",
            request.Country, request.Year, regime, request.GrossIncome);

        // Special case: flat-rate tax is a fixed payment — no calculation needed.
        if (regime == ETaxRegime.FlatRateTax)
        {
            return CalculateFlatRateTax(request, config);
        }

        // For all other regimes, calculate using the standard formula.
        return CalculateStandardRegime(request, config, regime);
    }

    /// <summary>
    /// Compares all available regimes for the same income and returns sorted results.
    /// </summary>
    public async Task<List<TaxEstimationResult>> CompareRegimesAsync(
        decimal grossIncome, string country, int year,
        string? activityType = null, bool isMainActivity = true,
        decimal? actualExpenses = null, CancellationToken ct = default)
    {
        // Determine which regimes are applicable for the country.
        var applicableRegimes = GetApplicableRegimes(country, grossIncome);

        var results = new List<TaxEstimationResult>();

        foreach (var regime in applicableRegimes)
        {
            try
            {
                var request = new TaxEstimationRequest
                {
                    GrossIncome = grossIncome,
                    Country = country,
                    Year = year,
                    TaxRegime = regime.ToString(),
                    ActivityType = activityType,
                    IsMainActivity = isMainActivity,
                    ActualExpenses = actualExpenses
                };

                var result = await EstimateAsync(request, ct);
                results.Add(result);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to calculate regime {Regime} for comparison", regime);
            }
        }

        // Sort by total obligations ascending (best option first).
        return results.OrderBy(r => r.TotalObligations).ToList();
    }

    public async Task<TaxYearConfigDto?> GetConfigAsync(
        string country, int year, CancellationToken ct = default)
    {
        var config = await _masterContext.Set<TaxYearConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Country == country && c.Year == year, ct);

        return config == null ? null : MapToDto(config);
    }

    public async Task<List<TaxYearConfigDto>> GetAllConfigsAsync(CancellationToken ct = default)
    {
        var configs = await _masterContext.Set<TaxYearConfig>()
            .AsNoTracking()
            .OrderByDescending(c => c.Year)
            .ThenBy(c => c.Country)
            .ToListAsync(ct);

        return configs.Select(MapToDto).ToList();
    }

    // ─── Private calculation methods ────────────────────────────────────

    /// <summary>
    /// CZ flat-rate tax: fixed monthly payment covering income tax + social + health.
    /// No calculation needed — just look up the band amount.
    /// </summary>
    private static TaxEstimationResult CalculateFlatRateTax(
        TaxEstimationRequest request, TaxYearConfig config)
    {
        // Default to band 1 if not specified.
        var bandMonthly = config.FlatRateBand1Monthly;
        var bandName = "Band1";

        // Determine band from gross income thresholds.
        if (request.GrossIncome > 1_500_000)
        {
            bandMonthly = config.FlatRateBand3Monthly;
            bandName = "Band3";
        }
        else if (request.GrossIncome > 1_000_000)
        {
            bandMonthly = config.FlatRateBand2Monthly;
            bandName = "Band2";
        }

        var annualTotal = bandMonthly * 12;

        return new TaxEstimationResult
        {
            GrossIncome = request.GrossIncome,
            Expenses = 0, // Not tracked in flat-rate regime
            TaxRegime = request.TaxRegime,
            Country = request.Country,
            Year = request.Year,
            CurrencyCode = config.CurrencyCode,
            TaxBase = request.GrossIncome, // Entire income — tax is flat
            IncomeTaxBeforeCredits = 0,
            TaxCredits = 0,
            IncomeTax = 0, // Included in the flat payment
            SocialAssessmentBase = 0,
            SocialInsurance = 0,
            HealthAssessmentBase = 0,
            HealthInsurance = 0,
            MonthlyAdvanceSocial = 0,
            MonthlyAdvanceHealth = 0,
            TotalObligations = annualTotal,
            TotalMonthlyObligations = bandMonthly,
            NetIncome = request.GrossIncome - annualTotal,
            EffectiveTaxRate = request.GrossIncome > 0
                ? Math.Round(annualTotal / request.GrossIncome * 100, 2) : 0,
            Steps = new List<TaxCalculationStep>
            {
                new()
                {
                    LabelKey = "Tax_Step_FlatRateBand",
                    Description = $"Flat-rate tax band: {bandName}",
                    Amount = bandMonthly,
                    Formula = $"{bandMonthly:N0} / month"
                },
                new()
                {
                    LabelKey = "Tax_Step_FlatRateAnnual",
                    Description = "Annual flat-rate payment (tax + social + health)",
                    Amount = annualTotal,
                    Formula = $"{bandMonthly:N0} x 12 = {annualTotal:N0}"
                }
            }
        };
    }

    /// <summary>
    /// Standard calculation for lump-sum, tax records, and full accounting regimes.
    /// Applies to both CZ and SK with country-specific adjustments.
    /// </summary>
    private static TaxEstimationResult CalculateStandardRegime(
        TaxEstimationRequest request, TaxYearConfig config, ETaxRegime regime)
    {
        var steps = new List<TaxCalculationStep>();
        var grossIncome = request.GrossIncome;

        // ── Step 1: Calculate expenses ──────────────────────────────────
        var expenses = CalculateExpenses(request, config, regime, steps);

        // ── Step 2: Tax base ────────────────────────────────────────────
        var taxBase = Math.Max(0, grossIncome - expenses);
        steps.Add(new TaxCalculationStep
        {
            LabelKey = "Tax_Step_TaxBase",
            Description = "Tax base (gross income - expenses)",
            Amount = taxBase,
            Formula = $"{grossIncome:N0} - {expenses:N0} = {taxBase:N0}"
        });

        // ── Step 3: Income tax ──────────────────────────────────────────
        var (incomeTaxBefore, incomeTaxAfter, taxCredits) =
            CalculateIncomeTax(taxBase, config, request.Country, steps);

        // ── Step 4: Social insurance ────────────────────────────────────
        var (socialBase, socialAnnual, socialMonthly) =
            CalculateSocialInsurance(taxBase, config, request.IsMainActivity, steps);

        // ── Step 5: Health insurance ────────────────────────────────────
        var (healthBase, healthAnnual, healthMonthly) =
            CalculateHealthInsurance(taxBase, config, request.IsMainActivity, steps);

        // ── Step 6: Totals ──────────────────────────────────────────────
        var totalObligations = incomeTaxAfter + socialAnnual + healthAnnual;
        var totalMonthly = incomeTaxAfter / 12 + socialMonthly + healthMonthly;
        var netIncome = grossIncome - expenses - totalObligations;
        var effectiveRate = grossIncome > 0
            ? Math.Round(totalObligations / grossIncome * 100, 2) : 0;

        steps.Add(new TaxCalculationStep
        {
            LabelKey = "Tax_Step_TotalObligations",
            Description = "Total annual obligations",
            Amount = totalObligations,
            Formula = $"{incomeTaxAfter:N0} + {socialAnnual:N0} + {healthAnnual:N0} = {totalObligations:N0}"
        });

        steps.Add(new TaxCalculationStep
        {
            LabelKey = "Tax_Step_NetIncome",
            Description = "Net income (gross - expenses - obligations)",
            Amount = netIncome,
            Formula = $"{grossIncome:N0} - {expenses:N0} - {totalObligations:N0} = {netIncome:N0}"
        });

        return new TaxEstimationResult
        {
            GrossIncome = grossIncome,
            Expenses = expenses,
            TaxRegime = request.TaxRegime,
            Country = request.Country,
            Year = request.Year,
            CurrencyCode = config.CurrencyCode,
            TaxBase = taxBase,
            IncomeTaxBeforeCredits = incomeTaxBefore,
            TaxCredits = taxCredits,
            IncomeTax = incomeTaxAfter,
            SocialAssessmentBase = socialBase,
            SocialInsurance = socialAnnual,
            MonthlyAdvanceSocial = socialMonthly,
            HealthAssessmentBase = healthBase,
            HealthInsurance = healthAnnual,
            MonthlyAdvanceHealth = healthMonthly,
            TotalObligations = totalObligations,
            TotalMonthlyObligations = Math.Round(totalMonthly, 0),
            NetIncome = netIncome,
            EffectiveTaxRate = effectiveRate,
            Steps = steps
        };
    }

    /// <summary>
    /// Calculates expenses based on the regime (lump-sum percentage or actual).
    /// </summary>
    private static decimal CalculateExpenses(
        TaxEstimationRequest request, TaxYearConfig config,
        ETaxRegime regime, List<TaxCalculationStep> steps)
    {
        decimal expenses;
        string formula;

        switch (regime)
        {
            case ETaxRegime.LumpSumExpenses80:
                expenses = Math.Min(request.GrossIncome * 0.80m, config.LumpSum80Cap);
                formula = $"min({request.GrossIncome:N0} x 80%, {config.LumpSum80Cap:N0}) = {expenses:N0}";
                break;

            case ETaxRegime.LumpSumExpenses60:
                expenses = Math.Min(request.GrossIncome * 0.60m, config.LumpSum60Cap);
                formula = $"min({request.GrossIncome:N0} x 60%, {config.LumpSum60Cap:N0}) = {expenses:N0}";
                break;

            case ETaxRegime.LumpSumExpenses40:
                expenses = Math.Min(request.GrossIncome * 0.40m, config.LumpSum40Cap);
                formula = $"min({request.GrossIncome:N0} x 40%, {config.LumpSum40Cap:N0}) = {expenses:N0}";
                break;

            case ETaxRegime.LumpSumExpenses30:
                expenses = Math.Min(request.GrossIncome * 0.30m, config.LumpSum30Cap);
                formula = $"min({request.GrossIncome:N0} x 30%, {config.LumpSum30Cap:N0}) = {expenses:N0}";
                break;

            case ETaxRegime.TaxRecords:
            case ETaxRegime.FullAccounting:
                expenses = request.ActualExpenses ?? 0;
                formula = $"Actual expenses: {expenses:N0}";
                break;

            default:
                expenses = 0;
                formula = "No expenses (flat-rate)";
                break;
        }

        steps.Add(new TaxCalculationStep
        {
            LabelKey = "Tax_Step_Expenses",
            Description = $"Expense deduction ({regime})",
            Amount = expenses,
            Formula = formula
        });

        return expenses;
    }

    /// <summary>
    /// Calculates income tax with progressive rate and taxpayer credit.
    /// Returns (taxBeforeCredits, taxAfterCredits, credits).
    /// </summary>
    private static (decimal Before, decimal After, decimal Credits) CalculateIncomeTax(
        decimal taxBase, TaxYearConfig config, string country,
        List<TaxCalculationStep> steps)
    {
        decimal taxBefore;
        var rate = config.IncomeTaxRate / 100m;
        var progressiveRate = config.ProgressiveTaxRate / 100m;

        if (taxBase > config.ProgressiveThreshold && config.ProgressiveThreshold > 0)
        {
            // Progressive tax: standard rate up to threshold, then higher rate above.
            var standardPortion = config.ProgressiveThreshold * rate;
            var progressivePortion = (taxBase - config.ProgressiveThreshold) * progressiveRate;
            taxBefore = standardPortion + progressivePortion;

            steps.Add(new TaxCalculationStep
            {
                LabelKey = "Tax_Step_IncomeTaxProgressive",
                Description = $"Income tax (progressive: {config.IncomeTaxRate}% + {config.ProgressiveTaxRate}%)",
                Amount = taxBefore,
                Formula = $"({config.ProgressiveThreshold:N0} x {config.IncomeTaxRate}%) + " +
                          $"({taxBase - config.ProgressiveThreshold:N0} x {config.ProgressiveTaxRate}%) = {taxBefore:N0}"
            });
        }
        else
        {
            // Flat tax rate.
            taxBefore = taxBase * rate;

            steps.Add(new TaxCalculationStep
            {
                LabelKey = "Tax_Step_IncomeTax",
                Description = $"Income tax ({config.IncomeTaxRate}%)",
                Amount = taxBefore,
                Formula = $"{taxBase:N0} x {config.IncomeTaxRate}% = {taxBefore:N0}"
            });
        }

        // Apply taxpayer credit.
        var credits = config.BasicTaxpayerCredit;
        var taxAfter = Math.Max(0, taxBefore - credits);

        steps.Add(new TaxCalculationStep
        {
            LabelKey = "Tax_Step_TaxpayerCredit",
            Description = "Basic taxpayer credit",
            Amount = credits,
            Formula = $"{taxBefore:N0} - {credits:N0} = {taxAfter:N0}"
        });

        return (Math.Round(taxBefore, 0), Math.Round(taxAfter, 0), credits);
    }

    /// <summary>
    /// Calculates social insurance: assessment base × rate, enforcing min/max.
    /// Returns (assessmentBase, annualAmount, monthlyAdvance).
    /// </summary>
    private static (decimal Base, decimal Annual, decimal Monthly) CalculateSocialInsurance(
        decimal taxBase, TaxYearConfig config, bool isMainActivity,
        List<TaxCalculationStep> steps)
    {
        var basePercent = config.SocialAssessmentBasePercent / 100m;
        var rate = config.SocialInsuranceRate / 100m;

        // Assessment base = percentage of tax base.
        var assessmentBase = taxBase * basePercent;

        // Monthly assessment base.
        var monthlyBase = assessmentBase / 12m;

        // Enforce minimum (different for main vs. secondary activity).
        var minMonthly = isMainActivity
            ? config.MinMonthlySocialMain
            : config.MinMonthlySocialSecondary;
        var minMonthlyPayment = minMonthly;

        // Calculate monthly payment.
        var monthlyPayment = monthlyBase * rate;

        // Enforce minimum.
        if (monthlyPayment < minMonthlyPayment)
            monthlyPayment = minMonthlyPayment;

        // Enforce maximum (if configured).
        if (config.MaxSocialAssessmentBase > 0)
        {
            var maxMonthlyBase = config.MaxSocialAssessmentBase / 12m;
            var maxMonthlyPayment = maxMonthlyBase * rate;
            if (monthlyPayment > maxMonthlyPayment)
                monthlyPayment = maxMonthlyPayment;
        }

        monthlyPayment = Math.Round(monthlyPayment, 0);
        var annualPayment = monthlyPayment * 12;

        steps.Add(new TaxCalculationStep
        {
            LabelKey = "Tax_Step_SocialInsurance",
            Description = $"Social insurance ({config.SocialInsuranceRate}% of {config.SocialAssessmentBasePercent}% of tax base)",
            Amount = annualPayment,
            Formula = $"Base: {taxBase:N0} x {config.SocialAssessmentBasePercent}% = {assessmentBase:N0}, " +
                      $"monthly: {monthlyPayment:N0} (min {minMonthlyPayment:N0})"
        });

        return (assessmentBase, annualPayment, monthlyPayment);
    }

    /// <summary>
    /// Calculates health insurance: assessment base × rate, enforcing minimum.
    /// Returns (assessmentBase, annualAmount, monthlyAdvance).
    /// </summary>
    private static (decimal Base, decimal Annual, decimal Monthly) CalculateHealthInsurance(
        decimal taxBase, TaxYearConfig config, bool isMainActivity,
        List<TaxCalculationStep> steps)
    {
        var basePercent = config.HealthAssessmentBasePercent / 100m;
        var rate = config.HealthInsuranceRate / 100m;

        // Assessment base = percentage of tax base.
        var assessmentBase = taxBase * basePercent;

        // Monthly assessment base.
        var monthlyBase = assessmentBase / 12m;

        // Calculate monthly payment.
        var monthlyPayment = monthlyBase * rate;

        // Enforce minimum (only for main activity).
        if (isMainActivity && monthlyPayment < config.MinMonthlyHealthMain)
            monthlyPayment = config.MinMonthlyHealthMain;

        // Enforce maximum (if configured, 0 = no cap).
        if (config.MaxHealthAssessmentBase > 0)
        {
            var maxMonthlyBase = config.MaxHealthAssessmentBase / 12m;
            var maxMonthlyPayment = maxMonthlyBase * rate;
            if (monthlyPayment > maxMonthlyPayment)
                monthlyPayment = maxMonthlyPayment;
        }

        monthlyPayment = Math.Round(monthlyPayment, 0);
        var annualPayment = monthlyPayment * 12;

        steps.Add(new TaxCalculationStep
        {
            LabelKey = "Tax_Step_HealthInsurance",
            Description = $"Health insurance ({config.HealthInsuranceRate}% of {config.HealthAssessmentBasePercent}% of tax base)",
            Amount = annualPayment,
            Formula = $"Base: {taxBase:N0} x {config.HealthAssessmentBasePercent}% = {assessmentBase:N0}, " +
                      $"monthly: {monthlyPayment:N0} (min {config.MinMonthlyHealthMain:N0})"
        });

        return (assessmentBase, annualPayment, monthlyPayment);
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns regimes applicable for a given country and income level.
    /// </summary>
    private static List<ETaxRegime> GetApplicableRegimes(string country, decimal grossIncome)
    {
        if (country == "SK")
        {
            return new List<ETaxRegime>
            {
                ETaxRegime.LumpSumExpenses60,
                ETaxRegime.TaxRecords,
                ETaxRegime.FullAccounting
            };
        }

        // CZ: all regimes, but flat-rate only for income <= 2M and non-VAT payers.
        var regimes = new List<ETaxRegime>();

        if (grossIncome <= 2_000_000)
            regimes.Add(ETaxRegime.FlatRateTax);

        regimes.AddRange(new[]
        {
            ETaxRegime.LumpSumExpenses80,
            ETaxRegime.LumpSumExpenses60,
            ETaxRegime.LumpSumExpenses40,
            ETaxRegime.LumpSumExpenses30,
            ETaxRegime.TaxRecords,
            ETaxRegime.FullAccounting
        });

        return regimes;
    }

    // ─── CRUD methods ─────────────────────────────────────────────────

    /// <summary>
    /// Creates a new TaxYearConfig. Throws if a config for (Country, Year) already exists.
    /// </summary>
    public async Task<TaxYearConfigDto> CreateConfigAsync(
        CreateTaxYearConfigDto dto, CancellationToken ct = default)
    {
        // Check for duplicate (Country, Year) combination.
        var exists = await _masterContext.Set<TaxYearConfig>()
            .AnyAsync(c => c.Country == dto.Country && c.Year == dto.Year, ct);

        if (exists)
        {
            throw new InvalidOperationException(
                $"Tax configuration for {dto.Country}/{dto.Year} already exists.");
        }

        var entity = MapFromCreateDto(dto);
        _masterContext.Set<TaxYearConfig>().Add(entity);
        await _masterContext.SaveChangesAsync(ct);

        _logger.LogInformation("Created TaxYearConfig: {Country}/{Year}, Id={Id}",
            entity.Country, entity.Year, entity.Id);

        return MapToDto(entity);
    }

    /// <summary>
    /// Updates an existing TaxYearConfig by ID.
    /// Returns the updated config, or null if not found.
    /// </summary>
    public async Task<TaxYearConfigDto?> UpdateConfigAsync(
        long id, CreateTaxYearConfigDto dto, CancellationToken ct = default)
    {
        var entity = await _masterContext.Set<TaxYearConfig>()
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (entity == null)
            return null;

        // Check for duplicate if Country/Year is changing.
        if (entity.Country != dto.Country || entity.Year != dto.Year)
        {
            var duplicate = await _masterContext.Set<TaxYearConfig>()
                .AnyAsync(c => c.Country == dto.Country && c.Year == dto.Year && c.Id != id, ct);

            if (duplicate)
            {
                throw new InvalidOperationException(
                    $"Tax configuration for {dto.Country}/{dto.Year} already exists.");
            }
        }

        // Update all fields from DTO.
        ApplyDtoToEntity(dto, entity);
        await _masterContext.SaveChangesAsync(ct);

        _logger.LogInformation("Updated TaxYearConfig Id={Id}: {Country}/{Year}",
            entity.Id, entity.Country, entity.Year);

        return MapToDto(entity);
    }

    /// <summary>
    /// Deletes a TaxYearConfig by ID. Returns true if deleted, false if not found.
    /// </summary>
    public async Task<bool> DeleteConfigAsync(long id, CancellationToken ct = default)
    {
        var entity = await _masterContext.Set<TaxYearConfig>()
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (entity == null)
            return false;

        _masterContext.Set<TaxYearConfig>().Remove(entity);
        await _masterContext.SaveChangesAsync(ct);

        _logger.LogInformation("Deleted TaxYearConfig Id={Id}: {Country}/{Year}",
            id, entity.Country, entity.Year);

        return true;
    }

    // ─── Annual income calculation ──────────────────────────────────────

    /// <summary>
    /// Calculates annual gross income from issued invoices for a given year.
    /// Only includes Completed and Paid invoices (not Drafts or Deleted).
    /// Uses IssueDate to determine the year.
    /// </summary>
    public async Task<AnnualIncomeDto> GetAnnualIncomeAsync(
        int year, CancellationToken ct = default)
    {
        // Query all issued invoices (not credit notes) that are completed or paid
        // and have an IssueDate in the requested year.
        var validStatuses = new[] { EInvoiceStatus.Completed, EInvoiceStatus.Paid };

        var invoices = await _tenantContext.Set<Invoice>()
            .AsNoTracking()
            .Where(i =>
                i.DocumentType == EDocumentType.Invoice &&
                validStatuses.Contains(i.Status) &&
                i.IssueDate.HasValue &&
                i.IssueDate.Value.Year == year)
            .Select(i => new
            {
                i.TotalBeforeVat,
                i.TotalWithVat,
                CurrencyCode = i.Currency.Code
            })
            .ToListAsync(ct);

        if (invoices.Count == 0)
        {
            return new AnnualIncomeDto
            {
                Year = year,
                GrossIncome = 0,
                GrossIncomeWithVat = 0,
                InvoiceCount = 0,
                CurrencyCode = "CZK"
            };
        }

        // Find the primary currency (the one with the highest total).
        var primaryCurrency = invoices
            .GroupBy(i => i.CurrencyCode)
            .OrderByDescending(g => g.Sum(i => i.TotalWithVat))
            .First()
            .Key;

        return new AnnualIncomeDto
        {
            Year = year,
            GrossIncome = invoices.Sum(i => i.TotalBeforeVat),
            GrossIncomeWithVat = invoices.Sum(i => i.TotalWithVat),
            InvoiceCount = invoices.Count,
            CurrencyCode = primaryCurrency
        };
    }

    // ─── Insurance advance notification ─────────────────────────────────

    /// <summary>
    /// Calculates upcoming insurance advance payments based on the company's
    /// tax settings and the current year's income.
    /// Returns null if the issuer has no tax regime configured.
    /// </summary>
    public async Task<InsuranceAdvanceDto?> GetInsuranceAdvanceAsync(
        CancellationToken ct = default)
    {
        // Get the issuer (company) from the tenant DB.
        var issuer = await _tenantContext.Set<Client>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.IsIssuer, ct);

        if (issuer == null || issuer.TaxRegime == null)
            return null;

        // Determine country from the issuer's address (fallback to "CZ").
        var country = "CZ"; // Default — could be derived from issuer address in the future

        var currentYear = DateTime.Today.Year;

        // Get the tax config for the current year.
        var config = await _masterContext.Set<TaxYearConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Country == country && c.Year == currentYear, ct);

        if (config == null)
            return null;

        // Calculate the current year's income to determine assessment base.
        var income = await GetAnnualIncomeAsync(currentYear, ct);

        // Estimate using the issuer's configured tax regime.
        var request = new TaxEstimationRequest
        {
            GrossIncome = income.GrossIncome > 0 ? income.GrossIncome : 0,
            Country = country,
            Year = currentYear,
            TaxRegime = issuer.TaxRegime.ToString()!,
            IsMainActivity = issuer.IsMainActivity
        };

        try
        {
            var estimation = await EstimateAsync(request, ct);

            // Insurance advances are due by the 20th of the following month.
            // Determine the next payment date.
            var today = DateTime.Today;
            var nextPaymentDate = new DateTime(today.Year, today.Month, 20);
            if (today.Day > 20)
            {
                // This month's deadline has passed — next is the 20th of next month.
                nextPaymentDate = nextPaymentDate.AddMonths(1);
            }

            return new InsuranceAdvanceDto
            {
                MonthlySocial = estimation.MonthlyAdvanceSocial,
                MonthlyHealth = estimation.MonthlyAdvanceHealth,
                MonthlyTotal = estimation.MonthlyAdvanceSocial + estimation.MonthlyAdvanceHealth,
                NextPaymentDate = nextPaymentDate,
                DaysUntilPayment = (nextPaymentDate - today).Days,
                CurrencyCode = config.CurrencyCode,
                TaxRegime = issuer.TaxRegime.ToString()!
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to calculate insurance advances");
            return null;
        }
    }

    // ─── Mapping helpers ────────────────────────────────────────────────

    /// <summary>
    /// Maps TaxYearConfig entity to DTO.
    /// </summary>
    private static TaxYearConfigDto MapToDto(TaxYearConfig config) => new()
    {
        Id = config.Id,
        Year = config.Year,
        Country = config.Country,
        CurrencyCode = config.CurrencyCode,
        AverageMonthlyWage = config.AverageMonthlyWage,
        LivingMinimum = config.LivingMinimum,
        IncomeTaxRate = config.IncomeTaxRate,
        ProgressiveTaxRate = config.ProgressiveTaxRate,
        ProgressiveThreshold = config.ProgressiveThreshold,
        BasicTaxpayerCredit = config.BasicTaxpayerCredit,
        SocialInsuranceRate = config.SocialInsuranceRate,
        SocialAssessmentBasePercent = config.SocialAssessmentBasePercent,
        MinMonthlySocialMain = config.MinMonthlySocialMain,
        MinMonthlySocialSecondary = config.MinMonthlySocialSecondary,
        MaxSocialAssessmentBase = config.MaxSocialAssessmentBase,
        HealthInsuranceRate = config.HealthInsuranceRate,
        HealthAssessmentBasePercent = config.HealthAssessmentBasePercent,
        MinMonthlyHealthMain = config.MinMonthlyHealthMain,
        MaxHealthAssessmentBase = config.MaxHealthAssessmentBase,
        FlatRateBand1Monthly = config.FlatRateBand1Monthly,
        FlatRateBand2Monthly = config.FlatRateBand2Monthly,
        FlatRateBand3Monthly = config.FlatRateBand3Monthly,
        LumpSum80Cap = config.LumpSum80Cap,
        LumpSum60Cap = config.LumpSum60Cap,
        LumpSum40Cap = config.LumpSum40Cap,
        LumpSum30Cap = config.LumpSum30Cap
    };

    /// <summary>
    /// Creates a new TaxYearConfig entity from a CreateTaxYearConfigDto.
    /// </summary>
    private static TaxYearConfig MapFromCreateDto(CreateTaxYearConfigDto dto) => new()
    {
        Year = dto.Year,
        Country = dto.Country,
        CurrencyCode = dto.CurrencyCode,
        AverageMonthlyWage = dto.AverageMonthlyWage,
        LivingMinimum = dto.LivingMinimum,
        IncomeTaxRate = dto.IncomeTaxRate,
        ProgressiveTaxRate = dto.ProgressiveTaxRate,
        ProgressiveThreshold = dto.ProgressiveThreshold,
        BasicTaxpayerCredit = dto.BasicTaxpayerCredit,
        SocialInsuranceRate = dto.SocialInsuranceRate,
        SocialAssessmentBasePercent = dto.SocialAssessmentBasePercent,
        MinMonthlySocialMain = dto.MinMonthlySocialMain,
        MinMonthlySocialSecondary = dto.MinMonthlySocialSecondary,
        MaxSocialAssessmentBase = dto.MaxSocialAssessmentBase,
        HealthInsuranceRate = dto.HealthInsuranceRate,
        HealthAssessmentBasePercent = dto.HealthAssessmentBasePercent,
        MinMonthlyHealthMain = dto.MinMonthlyHealthMain,
        MaxHealthAssessmentBase = dto.MaxHealthAssessmentBase,
        FlatRateBand1Monthly = dto.FlatRateBand1Monthly,
        FlatRateBand2Monthly = dto.FlatRateBand2Monthly,
        FlatRateBand3Monthly = dto.FlatRateBand3Monthly,
        LumpSum80Cap = dto.LumpSum80Cap,
        LumpSum60Cap = dto.LumpSum60Cap,
        LumpSum40Cap = dto.LumpSum40Cap,
        LumpSum30Cap = dto.LumpSum30Cap
    };

    /// <summary>
    /// Applies CreateTaxYearConfigDto values to an existing entity (for updates).
    /// </summary>
    private static void ApplyDtoToEntity(CreateTaxYearConfigDto dto, TaxYearConfig entity)
    {
        entity.Year = dto.Year;
        entity.Country = dto.Country;
        entity.CurrencyCode = dto.CurrencyCode;
        entity.AverageMonthlyWage = dto.AverageMonthlyWage;
        entity.LivingMinimum = dto.LivingMinimum;
        entity.IncomeTaxRate = dto.IncomeTaxRate;
        entity.ProgressiveTaxRate = dto.ProgressiveTaxRate;
        entity.ProgressiveThreshold = dto.ProgressiveThreshold;
        entity.BasicTaxpayerCredit = dto.BasicTaxpayerCredit;
        entity.SocialInsuranceRate = dto.SocialInsuranceRate;
        entity.SocialAssessmentBasePercent = dto.SocialAssessmentBasePercent;
        entity.MinMonthlySocialMain = dto.MinMonthlySocialMain;
        entity.MinMonthlySocialSecondary = dto.MinMonthlySocialSecondary;
        entity.MaxSocialAssessmentBase = dto.MaxSocialAssessmentBase;
        entity.HealthInsuranceRate = dto.HealthInsuranceRate;
        entity.HealthAssessmentBasePercent = dto.HealthAssessmentBasePercent;
        entity.MinMonthlyHealthMain = dto.MinMonthlyHealthMain;
        entity.MaxHealthAssessmentBase = dto.MaxHealthAssessmentBase;
        entity.FlatRateBand1Monthly = dto.FlatRateBand1Monthly;
        entity.FlatRateBand2Monthly = dto.FlatRateBand2Monthly;
        entity.FlatRateBand3Monthly = dto.FlatRateBand3Monthly;
        entity.LumpSum80Cap = dto.LumpSum80Cap;
        entity.LumpSum60Cap = dto.LumpSum60Cap;
        entity.LumpSum40Cap = dto.LumpSum40Cap;
        entity.LumpSum30Cap = dto.LumpSum30Cap;
    }
}
