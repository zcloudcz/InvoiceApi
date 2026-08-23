using Fakvio.Contracts.Dto.Readiness;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor service for the tenant readiness endpoint (<c>GET /api/readiness</c>, issue #209).
///
/// The report answers "what is still missing before this tenant can issue documents?".
/// The very same <c>ITenantReadinessService</c> rules also power the invoice completion gate
/// (issue #206), so what the banner lists is exactly what the API would refuse an issue
/// attempt with — the user just learns it before pressing the button instead of after.
/// </summary>
public class ReadinessApiService : ApiClientBase
{
    public ReadinessApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<ReadinessApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Fetches the readiness report, optionally narrowed to a single issuer.
    /// </summary>
    /// <param name="issuerId">
    /// When set, only this issuer is checked (an invoice knows which company issues it).
    /// When null, every issuer of the tenant is checked — what the dashboard wants.
    /// </param>
    /// <returns>
    /// The report, or an empty one when the call fails. Readiness is advisory decoration:
    /// a failing call must never take the hosting page down with it, so the caller simply
    /// sees "nothing to report" — same graceful degradation as <see cref="DashboardApiService"/>.
    /// </returns>
    public async Task<ReadinessReportDto> GetReportAsync(long? issuerId = null)
    {
        var url = issuerId.HasValue
            ? $"/api/readiness?issuerId={issuerId.Value}"
            : "/api/readiness";

        try
        {
            return await GetAsync<ReadinessReportDto>(url) ?? new ReadinessReportDto();
        }
        catch (ApiException)
        {
            // 401 never reaches here (UnauthorizedRedirectHandler redirects to /login).
            // 404 means the issuer id is stale — again nothing the user can act on from a banner.
            return new ReadinessReportDto();
        }
    }
}
