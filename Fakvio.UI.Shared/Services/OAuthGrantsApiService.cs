using System.Net;
using Fakvio.Contracts.Dto.OAuth;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor API client for "Připojené aplikace" (ADR 0001, docs/adr/0001-mcp-oauth21.md §4.8,
/// page /settings/integrations). Backing endpoints: <c>OAuthGrantsController</c>.
///
/// Both endpoints 404 while <c>McpOAuth:Enabled</c> is off — <see cref="GetAllAsync"/> turns
/// that into an empty list instead of an error, so the Integrations page can simply hide the
/// section instead of every user seeing a scary failure for a feature that is not live yet.
/// </summary>
public class OAuthGrantsApiService : ApiClientBase
{
    public OAuthGrantsApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<OAuthGrantsApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>The user's own live grants, or an empty list when the OAuth feature is not enabled.</summary>
    public async Task<IReadOnlyList<OAuthGrantDto>> GetAllAsync()
    {
        try
        {
            return await GetAsync<List<OAuthGrantDto>>("/api/oauth/grants") ?? [];
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }
    }

    /// <summary>Revokes a grant. False when it was already gone (unknown id, already revoked, or the feature is off).</summary>
    public async Task<bool> RevokeAsync(long id)
    {
        try
        {
            return await PostWithoutBodyBoolAsync($"/api/oauth/grants/{id}/revoke");
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
