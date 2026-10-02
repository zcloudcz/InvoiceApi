using Fakvio.Contracts.Dto.OssReport;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Client for the EU OSS quarterly report endpoints (rates live in VatRateApiService, the country preview in FakvioService).
/// See DEVGUIDE §4.16.
/// </summary>
public class OssApiService : ApiClientBase
{
    public OssApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<OssApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>Quarterly OSS report. Throws <see cref="ApiException"/> (message from the API, e.g. ECB unavailable).</summary>
    public Task<OssReportDto?> GetReportAsync(int year, int quarter)
        => GetAsync<OssReportDto>($"/api/oss-report?year={year}&quarter={quarter}");

    /// <summary>The same report as CSV bytes. Throws <see cref="ApiException"/> on failure.</summary>
    public Task<byte[]?> GetReportCsvAsync(int year, int quarter)
        => GetBytesAsync($"/api/oss-report/csv?year={year}&quarter={quarter}");
}
