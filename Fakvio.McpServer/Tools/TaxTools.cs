using System.ComponentModel;
using System.Text.Json;
using Fakvio.Contracts.Dto.Tax;
using Fakvio.McpServer.Client;
using ModelContextProtocol.Server;

namespace Fakvio.McpServer.Tools;

/// <summary>
/// MCP tools for tax estimation and regime comparison.
/// Provides income tax calculation, regime comparison, annual income lookup,
/// and insurance advance notifications.
///
/// Junior note: These tools let AI clients (like Claude) help users
/// understand their tax obligations and choose the best tax regime.
/// All calculations happen on the Fakvio API — these tools just relay requests.
/// </summary>
[McpServerToolType]
public static class TaxTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// Estimates tax obligations for a specific tax regime.
    /// Returns income tax, social insurance, health insurance, net income, and step-by-step breakdown.
    /// </summary>
    [McpServerTool, Description(
        "Estimate tax obligations for a self-employed person (OSVČ/SZČO). " +
        "Provide gross income, country (CZ/SK), year, and tax regime. " +
        "Returns income tax, social/health insurance, net income, and calculation steps. " +
        "Available regimes: FlatRateTax (CZ only, income<=2M), LumpSumExpenses80/60/40/30, TaxRecords, FullAccounting.")]
    public static async Task<string> EstimateTax(
        IFakvioApiClient api,
        [Description("Annual gross income (revenue before expenses)")] decimal grossIncome,
        [Description("Tax regime: FlatRateTax, LumpSumExpenses80, LumpSumExpenses60, LumpSumExpenses40, LumpSumExpenses30, TaxRecords, FullAccounting")] string taxRegime,
        [Description("Country code: CZ or SK")] string country = "CZ",
        [Description("Tax year (e.g., 2026)")] int year = 2026,
        [Description("Whether this is the main self-employed activity (affects minimum insurance)")] bool isMainActivity = true,
        [Description("Actual annual expenses (only for TaxRecords/FullAccounting regimes)")] decimal? actualExpenses = null,
        CancellationToken ct = default)
    {
        try
        {
            var result = await api.EstimateTaxAsync(new TaxEstimationRequest
            {
                GrossIncome = grossIncome,
                Country = country,
                Year = year,
                TaxRegime = taxRegime,
                IsMainActivity = isMainActivity,
                ActualExpenses = actualExpenses
            }, ct);

            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Compares all applicable tax regimes for given income — helps find the cheapest option.
    /// </summary>
    [McpServerTool, Description(
        "Compare all applicable tax regimes for a given income. " +
        "Returns all regimes sorted by total obligations (cheapest first). " +
        "Useful for advising which regime saves the most money.")]
    public static async Task<string> CompareTaxRegimes(
        IFakvioApiClient api,
        [Description("Annual gross income (revenue before expenses)")] decimal grossIncome,
        [Description("Country code: CZ or SK")] string country = "CZ",
        [Description("Tax year (e.g., 2026)")] int year = 2026,
        [Description("Whether this is the main self-employed activity")] bool isMainActivity = true,
        [Description("Actual expenses (for TaxRecords/FullAccounting comparison)")] decimal? actualExpenses = null,
        CancellationToken ct = default)
    {
        try
        {
            var result = await api.CompareTaxRegimesAsync(
                grossIncome, country, year, isMainActivity, actualExpenses, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Gets the annual gross income auto-calculated from issued invoices.
    /// </summary>
    [McpServerTool, Description(
        "Get the annual gross income calculated from issued invoices. " +
        "Sums TotalBeforeVat of all Completed/Paid invoices for the given year. " +
        "Returns gross income, invoice count, and currency.")]
    public static async Task<string> GetAnnualIncome(
        IFakvioApiClient api,
        [Description("Year to calculate income for (e.g., 2026)")] int year,
        CancellationToken ct = default)
    {
        try
        {
            var result = await api.GetAnnualIncomeAsync(year, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Gets upcoming insurance advance payment information.
    /// </summary>
    [McpServerTool, Description(
        "Get upcoming insurance advance payment notification. " +
        "Returns monthly social/health insurance amounts, next due date, and days until payment. " +
        "Based on company's configured tax regime and current year's income from invoices.")]
    public static async Task<string> GetInsuranceAdvance(
        IFakvioApiClient api,
        CancellationToken ct = default)
    {
        try
        {
            var result = await api.GetInsuranceAdvanceAsync(ct);
            if (result == null)
                return JsonSerializer.Serialize(new { message = "No tax regime configured for your company." }, JsonOptions);

            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Gets tax year configuration (rates and thresholds) for a specific country and year.
    /// </summary>
    [McpServerTool, Description(
        "Get tax year configuration (annual rates, thresholds, insurance minimums/maximums) " +
        "for a specific country and year. Useful for understanding the tax parameters.")]
    public static async Task<string> GetTaxConfig(
        IFakvioApiClient api,
        [Description("Country code: CZ or SK")] string country = "CZ",
        [Description("Tax year (e.g., 2026)")] int year = 2026,
        CancellationToken ct = default)
    {
        try
        {
            var result = await api.GetTaxConfigAsync(country, year, ct);
            if (result == null)
                return JsonSerializer.Serialize(new { error = $"No tax config found for {country}/{year}." }, JsonOptions);

            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }
}
