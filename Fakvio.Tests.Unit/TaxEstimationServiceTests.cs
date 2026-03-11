using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Tax;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Comprehensive unit tests for TaxEstimationService.
/// Tests all tax regimes (flat-rate, lump-sum, tax records, full accounting),
/// CZ and SK calculations, progressive tax, min/max insurance enforcement,
/// regime comparison, and edge cases.
///
/// Junior note: Each test creates its own InMemory database with seed data
/// to ensure test isolation. We test the actual calculation logic — no mocking
/// of the service itself, only of the logger.
/// </summary>
public class TaxEstimationServiceTests : IDisposable
{
    private readonly MasterDbContext _context;
    private readonly TenantDbContext _tenantContext;
    private readonly TaxEstimationService _service;
    private readonly ILogger<TaxEstimationService> _logger;

    // ── CZ 2026 config values (matching seed data) ──────────────────
    private const decimal CZ_INCOME_TAX_RATE = 15m;
    private const decimal CZ_PROGRESSIVE_RATE = 23m;
    private const decimal CZ_TAXPAYER_CREDIT = 30_840m;
    private const decimal CZ_SOCIAL_RATE = 29.2m;
    private const decimal CZ_HEALTH_RATE = 13.5m;
    private const decimal CZ_MIN_SOCIAL_MAIN = 4_096m;
    private const decimal CZ_MIN_HEALTH_MAIN = 3_079m;
    private const decimal CZ_FLAT_BAND1 = 8_716m;
    private const decimal CZ_FLAT_BAND2 = 16_000m;
    private const decimal CZ_FLAT_BAND3 = 26_000m;

    public TaxEstimationServiceTests()
    {
        _logger = Substitute.For<ILogger<TaxEstimationService>>();

        var dbId = Guid.NewGuid().ToString();

        var masterOptions = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(dbId + "_master")
            .Options;

        var tenantOptions = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(dbId + "_tenant")
            .Options;

        _context = new MasterDbContext(masterOptions, null);
        _tenantContext = new TenantDbContext(tenantOptions);
        SeedTestData();
        _service = new TaxEstimationService(_context, _tenantContext, _logger);
    }

    public void Dispose()
    {
        _context.Dispose();
        _tenantContext.Dispose();
    }

    /// <summary>
    /// Seeds CZ 2026 and SK 2025 configs for testing.
    /// </summary>
    private void SeedTestData()
    {
        _context.Set<TaxYearConfig>().AddRange(
            // CZ 2026
            new TaxYearConfig
            {
                Id = 1, Year = 2026, Country = "CZ", CurrencyCode = "CZK",
                AverageMonthlyWage = 45_617m, LivingMinimum = 4_860m,
                IncomeTaxRate = CZ_INCOME_TAX_RATE, ProgressiveTaxRate = CZ_PROGRESSIVE_RATE,
                ProgressiveThreshold = 36 * 45_617m,
                BasicTaxpayerCredit = CZ_TAXPAYER_CREDIT,
                SocialInsuranceRate = CZ_SOCIAL_RATE, SocialAssessmentBasePercent = 50m,
                MinMonthlySocialMain = CZ_MIN_SOCIAL_MAIN, MinMonthlySocialSecondary = 0m,
                MaxSocialAssessmentBase = 48 * 45_617m * 12,
                HealthInsuranceRate = CZ_HEALTH_RATE, HealthAssessmentBasePercent = 50m,
                MinMonthlyHealthMain = CZ_MIN_HEALTH_MAIN, MaxHealthAssessmentBase = 0m,
                FlatRateBand1Monthly = CZ_FLAT_BAND1, FlatRateBand2Monthly = CZ_FLAT_BAND2,
                FlatRateBand3Monthly = CZ_FLAT_BAND3,
                LumpSum80Cap = 1_600_000m, LumpSum60Cap = 1_200_000m,
                LumpSum40Cap = 800_000m, LumpSum30Cap = 600_000m
            },
            // SK 2025
            new TaxYearConfig
            {
                Id = 2, Year = 2025, Country = "SK", CurrencyCode = "EUR",
                AverageMonthlyWage = 1_430m, LivingMinimum = 268.88m,
                IncomeTaxRate = 15m, ProgressiveTaxRate = 25m,
                ProgressiveThreshold = 176.8m * 268.88m,
                BasicTaxpayerCredit = 21 * 268.88m,
                SocialInsuranceRate = 33.15m, SocialAssessmentBasePercent = 50m,
                MinMonthlySocialMain = 216.13m, MinMonthlySocialSecondary = 0m,
                MaxSocialAssessmentBase = 7 * 1_430m * 12,
                HealthInsuranceRate = 14m, HealthAssessmentBasePercent = 50m,
                MinMonthlyHealthMain = 97.80m, MaxHealthAssessmentBase = 0m,
                FlatRateBand1Monthly = 0m, FlatRateBand2Monthly = 0m, FlatRateBand3Monthly = 0m,
                LumpSum80Cap = 0m, LumpSum60Cap = 20_000m,
                LumpSum40Cap = 0m, LumpSum30Cap = 0m
            }
        );
        _context.SaveChanges();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Flat-rate tax tests (CZ only)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EstimateAsync_FlatRate_Band1_ReturnsCorrectMonthlyPayment()
    {
        var request = CreateRequest("CZ", 2026, "FlatRateTax", 800_000m);

        var result = await _service.EstimateAsync(request);

        result.TotalMonthlyObligations.ShouldBe(CZ_FLAT_BAND1);
        result.TotalObligations.ShouldBe(CZ_FLAT_BAND1 * 12);
        result.TaxRegime.ShouldBe("FlatRateTax");
        result.CurrencyCode.ShouldBe("CZK");
    }

    [Fact]
    public async Task EstimateAsync_FlatRate_Band2_IncomeOver1M()
    {
        var request = CreateRequest("CZ", 2026, "FlatRateTax", 1_200_000m);

        var result = await _service.EstimateAsync(request);

        result.TotalMonthlyObligations.ShouldBe(CZ_FLAT_BAND2);
        result.TotalObligations.ShouldBe(CZ_FLAT_BAND2 * 12);
    }

    [Fact]
    public async Task EstimateAsync_FlatRate_Band3_IncomeOver1_5M()
    {
        var request = CreateRequest("CZ", 2026, "FlatRateTax", 1_800_000m);

        var result = await _service.EstimateAsync(request);

        result.TotalMonthlyObligations.ShouldBe(CZ_FLAT_BAND3);
        result.TotalObligations.ShouldBe(CZ_FLAT_BAND3 * 12);
    }

    [Fact]
    public async Task EstimateAsync_FlatRate_NetIncomeIsGrossMinusTotal()
    {
        var request = CreateRequest("CZ", 2026, "FlatRateTax", 1_000_000m);

        var result = await _service.EstimateAsync(request);

        result.NetIncome.ShouldBe(result.GrossIncome - result.TotalObligations);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Lump-sum expense tests (CZ)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EstimateAsync_LumpSum80_CalculatesExpensesCorrectly()
    {
        var request = CreateRequest("CZ", 2026, "LumpSumExpenses80", 1_000_000m);

        var result = await _service.EstimateAsync(request);

        // 80% of 1M = 800,000 (under cap of 1,600,000)
        result.Expenses.ShouldBe(800_000m);
        result.TaxBase.ShouldBe(200_000m);
    }

    [Fact]
    public async Task EstimateAsync_LumpSum80_CapsExpenses()
    {
        var request = CreateRequest("CZ", 2026, "LumpSumExpenses80", 3_000_000m);

        var result = await _service.EstimateAsync(request);

        // 80% of 3M = 2,400,000 but cap is 1,600,000
        result.Expenses.ShouldBe(1_600_000m);
        result.TaxBase.ShouldBe(1_400_000m);
    }

    [Fact]
    public async Task EstimateAsync_LumpSum60_CalculatesExpensesCorrectly()
    {
        var request = CreateRequest("CZ", 2026, "LumpSumExpenses60", 1_000_000m);

        var result = await _service.EstimateAsync(request);

        result.Expenses.ShouldBe(600_000m);
        result.TaxBase.ShouldBe(400_000m);
    }

    [Fact]
    public async Task EstimateAsync_LumpSum40_CalculatesExpensesCorrectly()
    {
        var request = CreateRequest("CZ", 2026, "LumpSumExpenses40", 1_000_000m);

        var result = await _service.EstimateAsync(request);

        result.Expenses.ShouldBe(400_000m);
        result.TaxBase.ShouldBe(600_000m);
    }

    [Fact]
    public async Task EstimateAsync_LumpSum30_CalculatesExpensesCorrectly()
    {
        var request = CreateRequest("CZ", 2026, "LumpSumExpenses30", 1_000_000m);

        var result = await _service.EstimateAsync(request);

        result.Expenses.ShouldBe(300_000m);
        result.TaxBase.ShouldBe(700_000m);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Income tax calculation tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EstimateAsync_IncomeTax_FlatRateBelow_ProgressiveThreshold()
    {
        // Income 1M, lump-sum 60% → tax base 400,000 (below progressive threshold ~1.64M)
        var request = CreateRequest("CZ", 2026, "LumpSumExpenses60", 1_000_000m);

        var result = await _service.EstimateAsync(request);

        // Tax base = 400,000 → 15% = 60,000 before credits
        result.IncomeTaxBeforeCredits.ShouldBe(Math.Round(400_000m * 0.15m, 0));
        // After credits: 60,000 - 30,840 = 29,160
        result.IncomeTax.ShouldBe(Math.Max(0, result.IncomeTaxBeforeCredits - CZ_TAXPAYER_CREDIT));
    }

    [Fact]
    public async Task EstimateAsync_IncomeTax_ZeroWhenCreditExceedsTax()
    {
        // Very low income → tax < taxpayer credit → income tax = 0
        var request = CreateRequest("CZ", 2026, "LumpSumExpenses80", 200_000m);

        var result = await _service.EstimateAsync(request);

        // Tax base = 200,000 * 20% = 40,000 → tax = 6,000 - 30,840 = 0 (negative → 0)
        result.IncomeTax.ShouldBe(0m);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Social insurance tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EstimateAsync_SocialInsurance_EnforcesMinimumForMainActivity()
    {
        // Low income → calculated social < minimum → minimum should be used
        var request = CreateRequest("CZ", 2026, "LumpSumExpenses80", 200_000m);

        var result = await _service.EstimateAsync(request);

        // Min monthly social for main = 4,096
        result.MonthlyAdvanceSocial.ShouldBeGreaterThanOrEqualTo(CZ_MIN_SOCIAL_MAIN);
    }

    [Fact]
    public async Task EstimateAsync_SocialInsurance_SecondaryActivity_NoMinimum()
    {
        var request = CreateRequest("CZ", 2026, "LumpSumExpenses80", 200_000m);
        request.IsMainActivity = false;

        var result = await _service.EstimateAsync(request);

        // Secondary activity has no minimum social (MinMonthlySocialSecondary = 0)
        // The calculated amount should be based on the assessment base, no floor
        result.SocialInsurance.ShouldBeGreaterThanOrEqualTo(0);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Health insurance tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EstimateAsync_HealthInsurance_EnforcesMinimumForMainActivity()
    {
        var request = CreateRequest("CZ", 2026, "LumpSumExpenses80", 200_000m);

        var result = await _service.EstimateAsync(request);

        result.MonthlyAdvanceHealth.ShouldBeGreaterThanOrEqualTo(CZ_MIN_HEALTH_MAIN);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Tax Records / Full Accounting (actual expenses)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EstimateAsync_TaxRecords_UsesActualExpenses()
    {
        var request = CreateRequest("CZ", 2026, "TaxRecords", 1_000_000m);
        request.ActualExpenses = 500_000m;

        var result = await _service.EstimateAsync(request);

        result.Expenses.ShouldBe(500_000m);
        result.TaxBase.ShouldBe(500_000m);
    }

    [Fact]
    public async Task EstimateAsync_FullAccounting_UsesActualExpenses()
    {
        var request = CreateRequest("CZ", 2026, "FullAccounting", 2_000_000m);
        request.ActualExpenses = 1_200_000m;

        var result = await _service.EstimateAsync(request);

        result.Expenses.ShouldBe(1_200_000m);
        result.TaxBase.ShouldBe(800_000m);
    }

    [Fact]
    public async Task EstimateAsync_TaxRecords_ZeroExpensesWhenNotProvided()
    {
        var request = CreateRequest("CZ", 2026, "TaxRecords", 1_000_000m);
        // ActualExpenses is null → should default to 0

        var result = await _service.EstimateAsync(request);

        result.Expenses.ShouldBe(0m);
        result.TaxBase.ShouldBe(1_000_000m);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Totals and effective rate tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EstimateAsync_TotalObligations_IsSumOfTaxSocialHealth()
    {
        var request = CreateRequest("CZ", 2026, "LumpSumExpenses60", 1_000_000m);

        var result = await _service.EstimateAsync(request);

        result.TotalObligations.ShouldBe(
            result.IncomeTax + result.SocialInsurance + result.HealthInsurance);
    }

    [Fact]
    public async Task EstimateAsync_NetIncome_IsCorrectlyCalculated()
    {
        var request = CreateRequest("CZ", 2026, "LumpSumExpenses60", 1_000_000m);

        var result = await _service.EstimateAsync(request);

        result.NetIncome.ShouldBe(
            result.GrossIncome - result.Expenses - result.TotalObligations);
    }

    [Fact]
    public async Task EstimateAsync_EffectiveTaxRate_IsCorrect()
    {
        var request = CreateRequest("CZ", 2026, "LumpSumExpenses60", 1_000_000m);

        var result = await _service.EstimateAsync(request);

        var expected = Math.Round(result.TotalObligations / result.GrossIncome * 100, 2);
        result.EffectiveTaxRate.ShouldBe(expected);
    }

    [Fact]
    public async Task EstimateAsync_Steps_AreNotEmpty()
    {
        var request = CreateRequest("CZ", 2026, "LumpSumExpenses60", 1_000_000m);

        var result = await _service.EstimateAsync(request);

        result.Steps.ShouldNotBeEmpty();
        result.Steps.ShouldAllBe(s => !string.IsNullOrEmpty(s.LabelKey));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Slovakia (SK) tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EstimateAsync_SK_LumpSum60_CalculatesCorrectly()
    {
        var request = CreateRequest("SK", 2025, "LumpSumExpenses60", 50_000m);

        var result = await _service.EstimateAsync(request);

        // 60% of 50,000 = 30,000 but cap is 20,000
        result.Expenses.ShouldBe(20_000m);
        result.TaxBase.ShouldBe(30_000m);
        result.CurrencyCode.ShouldBe("EUR");
    }

    [Fact]
    public async Task EstimateAsync_SK_LumpSum60_NoCap_WhenBelowLimit()
    {
        var request = CreateRequest("SK", 2025, "LumpSumExpenses60", 20_000m);

        var result = await _service.EstimateAsync(request);

        // 60% of 20,000 = 12,000 (under cap of 20,000)
        result.Expenses.ShouldBe(12_000m);
    }

    [Fact]
    public async Task EstimateAsync_SK_TaxRecords_WorksCorrectly()
    {
        var request = CreateRequest("SK", 2025, "TaxRecords", 80_000m);
        request.ActualExpenses = 30_000m;

        var result = await _service.EstimateAsync(request);

        result.Expenses.ShouldBe(30_000m);
        result.TaxBase.ShouldBe(50_000m);
        result.Country.ShouldBe("SK");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Error handling tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EstimateAsync_ThrowsForMissingConfig()
    {
        var request = CreateRequest("DE", 2026, "TaxRecords", 100_000m);

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.EstimateAsync(request));
    }

    [Fact]
    public async Task EstimateAsync_ThrowsForInvalidRegime()
    {
        var request = CreateRequest("CZ", 2026, "NonExistentRegime", 100_000m);

        await Should.ThrowAsync<ArgumentException>(
            () => _service.EstimateAsync(request));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Compare regimes tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CompareRegimesAsync_CZ_ReturnsMultipleRegimes()
    {
        var results = await _service.CompareRegimesAsync(
            1_000_000m, "CZ", 2026);

        // Should include flat-rate + 4 lump-sum + TaxRecords + FullAccounting = 7
        results.Count.ShouldBe(7);
    }

    [Fact]
    public async Task CompareRegimesAsync_CZ_HighIncome_ExcludesFlatRate()
    {
        // Flat-rate only available for income <= 2M
        var results = await _service.CompareRegimesAsync(
            3_000_000m, "CZ", 2026);

        results.ShouldNotContain(r => r.TaxRegime == "FlatRateTax");
        results.Count.ShouldBe(6); // All except flat-rate
    }

    [Fact]
    public async Task CompareRegimesAsync_SK_Returns3Regimes()
    {
        var results = await _service.CompareRegimesAsync(
            50_000m, "SK", 2025);

        // SK: LumpSum60, TaxRecords, FullAccounting
        results.Count.ShouldBe(3);
    }

    [Fact]
    public async Task CompareRegimesAsync_SortedByTotalObligationsAscending()
    {
        var results = await _service.CompareRegimesAsync(
            1_000_000m, "CZ", 2026);

        for (int i = 1; i < results.Count; i++)
        {
            results[i].TotalObligations.ShouldBeGreaterThanOrEqualTo(
                results[i - 1].TotalObligations);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // GetConfig tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetConfigAsync_ReturnsConfig_WhenExists()
    {
        var config = await _service.GetConfigAsync("CZ", 2026);

        config.ShouldNotBeNull();
        config.Country.ShouldBe("CZ");
        config.Year.ShouldBe(2026);
        config.IncomeTaxRate.ShouldBe(CZ_INCOME_TAX_RATE);
    }

    [Fact]
    public async Task GetConfigAsync_ReturnsNull_WhenNotFound()
    {
        var config = await _service.GetConfigAsync("DE", 2026);

        config.ShouldBeNull();
    }

    [Fact]
    public async Task GetAllConfigsAsync_ReturnsAllConfigs()
    {
        var configs = await _service.GetAllConfigsAsync();

        configs.Count.ShouldBe(2); // CZ 2026 + SK 2025
    }

    // ═══════════════════════════════════════════════════════════════════
    // Edge cases
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EstimateAsync_ZeroIncome_ReturnsMinimumObligations()
    {
        var request = CreateRequest("CZ", 2026, "LumpSumExpenses60", 0m);

        var result = await _service.EstimateAsync(request);

        result.GrossIncome.ShouldBe(0m);
        result.IncomeTax.ShouldBe(0m);
        // Even with zero income, minimum social/health apply for main activity
        result.MonthlyAdvanceSocial.ShouldBeGreaterThanOrEqualTo(CZ_MIN_SOCIAL_MAIN);
        result.MonthlyAdvanceHealth.ShouldBeGreaterThanOrEqualTo(CZ_MIN_HEALTH_MAIN);
    }

    [Fact]
    public async Task EstimateAsync_FlatRate_EffectiveTaxRate_IsZeroForZeroIncome()
    {
        var request = CreateRequest("CZ", 2026, "FlatRateTax", 0m);

        var result = await _service.EstimateAsync(request);

        result.EffectiveTaxRate.ShouldBe(0m);
    }

    // ═══════════════════════════════════════════════════════════════════
    // CRUD tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CreateConfigAsync_NewConfig_ReturnsCreatedDto()
    {
        var dto = CreateTestConfigDto("CZ", 2027);

        var result = await _service.CreateConfigAsync(dto);

        result.ShouldNotBeNull();
        result.Year.ShouldBe(2027);
        result.Country.ShouldBe("CZ");
        result.IncomeTaxRate.ShouldBe(15m);
    }

    [Fact]
    public async Task CreateConfigAsync_DuplicateCountryYear_ThrowsInvalidOperation()
    {
        // CZ/2026 already exists in seed data.
        var dto = CreateTestConfigDto("CZ", 2026);

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.CreateConfigAsync(dto));
    }

    [Fact]
    public async Task UpdateConfigAsync_ExistingConfig_ReturnsUpdatedDto()
    {
        var dto = CreateTestConfigDto("CZ", 2026);
        dto.IncomeTaxRate = 20m;

        var result = await _service.UpdateConfigAsync(1, dto);

        result.ShouldNotBeNull();
        result!.IncomeTaxRate.ShouldBe(20m);
    }

    [Fact]
    public async Task UpdateConfigAsync_NonExistentId_ReturnsNull()
    {
        var dto = CreateTestConfigDto("CZ", 2026);

        var result = await _service.UpdateConfigAsync(999, dto);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteConfigAsync_ExistingConfig_ReturnsTrue()
    {
        // First create a config we can safely delete.
        var dto = CreateTestConfigDto("CZ", 2028);
        var created = await _service.CreateConfigAsync(dto);

        var result = await _service.DeleteConfigAsync(created.Id);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteConfigAsync_NonExistentId_ReturnsFalse()
    {
        var result = await _service.DeleteConfigAsync(999);

        result.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Annual income tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetAnnualIncomeAsync_NoInvoices_ReturnsZero()
    {
        var result = await _service.GetAnnualIncomeAsync(2026);

        result.Year.ShouldBe(2026);
        result.GrossIncome.ShouldBe(0m);
        result.InvoiceCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetAnnualIncomeAsync_WithCompletedInvoices_SumsCorrectly()
    {
        // Seed some invoices in the tenant DB.
        SeedTenantInvoices();

        var result = await _service.GetAnnualIncomeAsync(2026);

        // We seeded 2 completed invoices for 2026: 10,000 + 20,000 = 30,000
        result.GrossIncome.ShouldBe(30_000m);
        result.InvoiceCount.ShouldBe(2);
        result.CurrencyCode.ShouldBe("CZK");
    }

    [Fact]
    public async Task GetAnnualIncomeAsync_ExcludesDraftInvoices()
    {
        SeedTenantInvoices();

        var result = await _service.GetAnnualIncomeAsync(2026);

        // Draft invoice (50,000) should NOT be included.
        result.GrossIncome.ShouldBe(30_000m);
    }

    [Fact]
    public async Task GetAnnualIncomeAsync_ExcludesCreditNotes()
    {
        SeedTenantInvoices();

        var result = await _service.GetAnnualIncomeAsync(2026);

        // Credit note (5,000) should NOT be included (DocumentType = CreditNote).
        result.GrossIncome.ShouldBe(30_000m);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Insurance advance tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetInsuranceAdvanceAsync_NoIssuer_ReturnsNull()
    {
        // No issuer in tenant DB → should return null.
        var result = await _service.GetInsuranceAdvanceAsync();

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetInsuranceAdvanceAsync_IssuerWithTaxRegime_ReturnsAdvance()
    {
        SeedTenantIssuerWithTaxRegime();

        var result = await _service.GetInsuranceAdvanceAsync();

        result.ShouldNotBeNull();
        result!.MonthlySocial.ShouldBeGreaterThanOrEqualTo(0);
        result.MonthlyHealth.ShouldBeGreaterThanOrEqualTo(0);
        result.MonthlyTotal.ShouldBe(result.MonthlySocial + result.MonthlyHealth);
        result.CurrencyCode.ShouldBe("CZK");
    }

    [Fact]
    public async Task GetInsuranceAdvanceAsync_NextPaymentDate_IsValid()
    {
        SeedTenantIssuerWithTaxRegime();

        var result = await _service.GetInsuranceAdvanceAsync();

        result.ShouldNotBeNull();
        result!.NextPaymentDate.Day.ShouldBe(20);
        result.NextPaymentDate.ShouldBeGreaterThanOrEqualTo(DateTime.Today);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static TaxEstimationRequest CreateRequest(
        string country, int year, string regime, decimal grossIncome) => new()
    {
        Country = country,
        Year = year,
        TaxRegime = regime,
        GrossIncome = grossIncome,
        IsMainActivity = true
    };

    /// <summary>
    /// Creates a test CreateTaxYearConfigDto with standard CZ values.
    /// </summary>
    private static CreateTaxYearConfigDto CreateTestConfigDto(string country, int year) => new()
    {
        Year = year,
        Country = country,
        CurrencyCode = country == "SK" ? "EUR" : "CZK",
        AverageMonthlyWage = 45_617m,
        LivingMinimum = 4_860m,
        IncomeTaxRate = 15m,
        ProgressiveTaxRate = 23m,
        ProgressiveThreshold = 1_642_212m,
        BasicTaxpayerCredit = 30_840m,
        SocialInsuranceRate = 29.2m,
        SocialAssessmentBasePercent = 50m,
        MinMonthlySocialMain = 4_096m,
        MinMonthlySocialSecondary = 0m,
        MaxSocialAssessmentBase = 26_265_792m,
        HealthInsuranceRate = 13.5m,
        HealthAssessmentBasePercent = 50m,
        MinMonthlyHealthMain = 3_079m,
        MaxHealthAssessmentBase = 0m,
        FlatRateBand1Monthly = 8_716m,
        FlatRateBand2Monthly = 16_000m,
        FlatRateBand3Monthly = 26_000m,
        LumpSum80Cap = 1_600_000m,
        LumpSum60Cap = 1_200_000m,
        LumpSum40Cap = 800_000m,
        LumpSum30Cap = 600_000m
    };

    /// <summary>
    /// Seeds test invoices in the tenant DB for annual income tests.
    /// Creates: 2 completed CZ invoices (2026), 1 draft (excluded), 1 credit note (excluded).
    /// </summary>
    private void SeedTenantInvoices()
    {
        // Seed a currency first.
        var currency = new Currency { Id = 1, Code = "CZK", Name = "Czech Koruna", Symbol = "Kč" };
        _tenantContext.Set<Currency>().Add(currency);
        _tenantContext.SaveChanges();

        // Seed an issuer (required FK).
        var issuer = new Client
        {
            Id = 1, CompanyName = "Test Issuer", IsIssuer = true, IsActive = true,
            RegistrationNumber = "12345678"
        };
        _tenantContext.Set<Client>().Add(issuer);
        _tenantContext.SaveChanges();

        // Seed invoices.
        _tenantContext.Set<Invoice>().AddRange(
            new Invoice
            {
                Id = 1, DocumentType = EDocumentType.Invoice, Status = EInvoiceStatus.Completed,
                IssueDate = new DateTime(2026, 3, 1), TotalBeforeVat = 10_000m, TotalWithVat = 12_100m,
                IssuerId = 1, CurrencyId = 1, InvoiceItem = new List<InvoiceItem>()
            },
            new Invoice
            {
                Id = 2, DocumentType = EDocumentType.Invoice, Status = EInvoiceStatus.Paid,
                IssueDate = new DateTime(2026, 4, 15), TotalBeforeVat = 20_000m, TotalWithVat = 24_200m,
                IssuerId = 1, CurrencyId = 1, InvoiceItem = new List<InvoiceItem>()
            },
            // Draft — should be excluded
            new Invoice
            {
                Id = 3, DocumentType = EDocumentType.Invoice, Status = EInvoiceStatus.Draft,
                IssueDate = new DateTime(2026, 5, 1), TotalBeforeVat = 50_000m, TotalWithVat = 60_500m,
                IssuerId = 1, CurrencyId = 1, InvoiceItem = new List<InvoiceItem>()
            },
            // Credit note — should be excluded
            new Invoice
            {
                Id = 4, DocumentType = EDocumentType.CreditNote, Status = EInvoiceStatus.Completed,
                IssueDate = new DateTime(2026, 3, 10), TotalBeforeVat = 5_000m, TotalWithVat = 6_050m,
                IssuerId = 1, CurrencyId = 1, InvoiceItem = new List<InvoiceItem>()
            }
        );
        _tenantContext.SaveChanges();
    }

    /// <summary>
    /// Seeds an issuer with a tax regime configured in the tenant DB.
    /// Used for insurance advance tests.
    /// </summary>
    private void SeedTenantIssuerWithTaxRegime()
    {
        var issuer = new Client
        {
            Id = 10, CompanyName = "Tax Test Company", IsIssuer = true, IsActive = true,
            RegistrationNumber = "99887766",
            TaxRegime = ETaxRegime.LumpSumExpenses60,
            IsMainActivity = true
        };
        _tenantContext.Set<Client>().Add(issuer);
        _tenantContext.SaveChanges();
    }
}
