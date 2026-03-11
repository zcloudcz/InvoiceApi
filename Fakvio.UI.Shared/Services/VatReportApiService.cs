using Fakvio.Contracts.Dto.VatReport;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor service for communicating with the VAT Report API endpoint.
/// Fetches aggregated VAT data for a given period.
/// Inherits ApiClientBase for shared auth, logging, impersonation, and error handling.
/// </summary>
public class VatReportApiService : ApiClientBase
{
    public VatReportApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<VatReportApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Fetches a VAT report for the specified period.
    /// Returns output VAT, input VAT, tax liability, revenue, expenses, and profit.
    /// </summary>
    public async Task<VatReportDto?> GetReportAsync(DateTime from, DateTime to)
    {
        try
        {
            return await GetAsync<VatReportDto>(
                $"/api/vat-report?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
        }
        catch (ApiException)
        {
            return null;
        }
    }
}
