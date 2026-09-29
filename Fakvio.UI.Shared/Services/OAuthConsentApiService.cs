using Fakvio.Contracts.Dto.OAuth;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor API client for the OAuth consent screen (ADR 0001, docs/adr/0001-mcp-oauth21.md
/// §4.2/§4.9, page /oauth/consent). Backing endpoints: <c>OAuthConsentController</c>.
/// </summary>
public class OAuthConsentApiService : ApiClientBase
{
    public OAuthConsentApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<OAuthConsentApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>Describes what a consent ticket is asking for. Null on an invalid/expired ticket.</summary>
    public Task<OAuthConsentInfoDto?> DescribeAsync(string ticket)
        => GetAsync<OAuthConsentInfoDto>($"/api/oauth/consent/{Uri.EscapeDataString(ticket)}");

    /// <summary>Records Allow/Deny. Returns the URL to navigate the browser to next (the client's redirect_uri).</summary>
    public Task<OAuthConsentDecisionResultDto?> DecideAsync(OAuthConsentDecisionDto decision)
        => PostAsync<OAuthConsentDecisionDto, OAuthConsentDecisionResultDto>("/api/oauth/consent/decision", decision);
}
