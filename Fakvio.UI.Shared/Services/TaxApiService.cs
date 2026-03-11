using Fakvio.Contracts.Dto.Tax;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor service for communicating with the Tax Estimation API endpoints.
/// Provides tax estimation, regime comparison, and configuration management.
/// Inherits ApiClientBase for shared auth, logging, impersonation, and error handling.
///
/// Junior note: This service calls TaxController endpoints on the API backend.
/// All calculation logic lives on the server — the UI only sends inputs and displays results.
/// </summary>
public class TaxApiService : ApiClientBase
{
    public TaxApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<TaxApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Estimates tax obligations for a single regime.
    /// Returns detailed breakdown including income tax, social/health insurance, and net income.
    /// </summary>
    public async Task<TaxEstimationResult?> EstimateAsync(TaxEstimationRequest request)
    {
        try
        {
            return await PostAsync<TaxEstimationRequest, TaxEstimationResult>("/api/tax/estimate", request);
        }
        catch (ApiException)
        {
            return null;
        }
    }

    /// <summary>
    /// Compares all applicable tax regimes for given income, sorted by total obligations (best first).
    /// </summary>
    public async Task<List<TaxEstimationResult>> CompareRegimesAsync(
        decimal grossIncome, string country = "CZ", int year = 2026,
        string? activityType = null, bool isMainActivity = true, decimal? actualExpenses = null)
    {
        try
        {
            var url = $"/api/tax/compare?grossIncome={grossIncome}&country={country}&year={year}" +
                      $"&isMainActivity={isMainActivity}";

            if (!string.IsNullOrEmpty(activityType))
                url += $"&activityType={activityType}";

            if (actualExpenses.HasValue)
                url += $"&actualExpenses={actualExpenses.Value}";

            return await GetAsync<List<TaxEstimationResult>>(url) ?? new();
        }
        catch (ApiException)
        {
            return new();
        }
    }

    /// <summary>
    /// Returns tax year configuration for a specific country and year.
    /// </summary>
    public async Task<TaxYearConfigDto?> GetConfigAsync(string country, int year)
    {
        try
        {
            return await GetAsync<TaxYearConfigDto>($"/api/tax/config/{country}/{year}");
        }
        catch (ApiException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns all available tax year configurations.
    /// </summary>
    public async Task<List<TaxYearConfigDto>> GetAllConfigsAsync()
    {
        try
        {
            return await GetAsync<List<TaxYearConfigDto>>("/api/tax/configs") ?? new();
        }
        catch (ApiException)
        {
            return new();
        }
    }

    /// <summary>
    /// Creates a new tax year configuration. Requires Admin/SysAdmin role.
    /// </summary>
    public async Task<TaxYearConfigDto?> CreateConfigAsync(CreateTaxYearConfigDto dto)
    {
        try
        {
            return await PostAsync<CreateTaxYearConfigDto, TaxYearConfigDto>("/api/tax/config", dto);
        }
        catch (ApiException)
        {
            return null;
        }
    }

    /// <summary>
    /// Updates an existing tax year configuration by ID. Requires Admin/SysAdmin role.
    /// </summary>
    public async Task<TaxYearConfigDto?> UpdateConfigAsync(long id, CreateTaxYearConfigDto dto)
    {
        try
        {
            return await PutAsync<CreateTaxYearConfigDto, TaxYearConfigDto>($"/api/tax/config/{id}", dto);
        }
        catch (ApiException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes a tax year configuration by ID. Requires Admin/SysAdmin role.
    /// </summary>
    public async Task<bool> DeleteConfigAsync(long id)
    {
        try
        {
            await DeleteAsync($"/api/tax/config/{id}");
            return true;
        }
        catch (ApiException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the annual gross income from issued invoices for a given year.
    /// </summary>
    public async Task<AnnualIncomeDto?> GetAnnualIncomeAsync(int year)
    {
        try
        {
            return await GetAsync<AnnualIncomeDto>($"/api/tax/income/{year}");
        }
        catch (ApiException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns upcoming insurance advance payment notification.
    /// Returns null if no tax regime is configured.
    /// </summary>
    public async Task<InsuranceAdvanceDto?> GetInsuranceAdvanceAsync()
    {
        try
        {
            return await GetAsync<InsuranceAdvanceDto>("/api/tax/insurance-advance");
        }
        catch (ApiException)
        {
            return null;
        }
    }
}
