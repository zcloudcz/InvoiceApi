using Fakvio.Contracts.Dto.Tax;

namespace Fakvio.Application.Service;

/// <summary>
/// Service for estimating tax obligations (income tax, social insurance, health insurance)
/// based on the selected tax regime, gross income, and country-specific rates.
///
/// Junior note: This service uses TaxYearConfig to get the annual rates and thresholds,
/// then applies the formulas specific to each tax regime (flat-rate, lump-sum, actual expenses).
/// </summary>
public interface ITaxEstimationService
{
    /// <summary>
    /// Calculates estimated tax obligations for the given inputs.
    /// Returns a detailed breakdown including income tax, social/health insurance, and net income.
    /// </summary>
    Task<TaxEstimationResult> EstimateAsync(TaxEstimationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Compares all available tax regimes for a given income and returns results for each.
    /// Useful for helping the user choose the most advantageous regime.
    /// </summary>
    Task<List<TaxEstimationResult>> CompareRegimesAsync(
        decimal grossIncome, string country, int year,
        string? activityType = null, bool isMainActivity = true,
        decimal? actualExpenses = null, CancellationToken ct = default);

    /// <summary>
    /// Returns the TaxYearConfig for a given country and year.
    /// Returns null if no configuration exists for the requested combination.
    /// </summary>
    Task<TaxYearConfigDto?> GetConfigAsync(string country, int year, CancellationToken ct = default);

    /// <summary>
    /// Returns all available TaxYearConfigs.
    /// </summary>
    Task<List<TaxYearConfigDto>> GetAllConfigsAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates a new TaxYearConfig record.
    /// Throws if a config for the same (Country, Year) already exists.
    /// </summary>
    Task<TaxYearConfigDto> CreateConfigAsync(CreateTaxYearConfigDto dto, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing TaxYearConfig record by ID.
    /// Returns the updated config, or null if the ID was not found.
    /// </summary>
    Task<TaxYearConfigDto?> UpdateConfigAsync(long id, CreateTaxYearConfigDto dto, CancellationToken ct = default);

    /// <summary>
    /// Deletes a TaxYearConfig record by ID.
    /// Returns true if deleted, false if not found.
    /// </summary>
    Task<bool> DeleteConfigAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Calculates the annual gross income from issued invoices for a given year.
    /// Sums TotalBeforeVat of all Completed/Paid invoices with IssueDate in the given year.
    /// </summary>
    Task<AnnualIncomeDto> GetAnnualIncomeAsync(int year, CancellationToken ct = default);

    /// <summary>
    /// Calculates the upcoming insurance advance payments based on company tax settings.
    /// Returns the monthly social/health amounts and the next payment due date.
    /// </summary>
    Task<InsuranceAdvanceDto?> GetInsuranceAdvanceAsync(CancellationToken ct = default);
}
