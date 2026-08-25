using Fakvio.Contracts.Dto.ApiKey;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor API client for the current user's API keys (page /settings/integrations).
/// Inherits <see cref="ApiClientBase"/> for shared auth headers, logging and error handling.
///
/// Backing endpoints (ApiKeyController):
/// - GET  /api/api-key            → list (prefix only, never the key)
/// - POST /api/api-key            → create (the ONLY response that carries the raw key)
/// - POST /api/api-key/{id}/revoke → soft revoke
/// </summary>
public class ApiKeyApiService : ApiClientBase
{
    public ApiKeyApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<ApiKeyApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Base URL of the API this client talks to. The integrations page pastes it into the
    /// generated MCP config snippets, so the user does not have to know their own API host.
    /// </summary>
    public string ApiBaseUrl => _httpClient.BaseAddress?.ToString().TrimEnd('/') ?? string.Empty;

    /// <summary>
    /// Lists the current user's API keys, newest first. Never returns a usable credential.
    /// </summary>
    public async Task<IReadOnlyList<ApiKeyDto>> GetAllAsync()
        => await GetAsync<List<ApiKeyDto>>("/api/api-key") ?? [];

    /// <summary>
    /// Creates a new API key. The returned <see cref="CreatedApiKeyDto.Key"/> is the raw key —
    /// it exists only in this response and is unrecoverable afterwards, so the caller must show
    /// it to the user immediately and must never persist or log it.
    /// </summary>
    public Task<CreatedApiKeyDto?> CreateAsync(CreateApiKeyDto dto)
        => PostAsync<CreateApiKeyDto, CreatedApiKeyDto>("/api/api-key", dto);

    /// <summary>
    /// Revokes one of the current user's keys. Returns false when the API answered 404
    /// (unknown key, someone else's key, or already revoked).
    /// </summary>
    public async Task<bool> RevokeAsync(long id)
    {
        try
        {
            return await PostWithoutBodyBoolAsync($"/api/api-key/{id}/revoke");
        }
        catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already revoked or gone — the caller only needs to refresh the list, not to
            // surface a scary error. Every other status still bubbles up.
            return false;
        }
    }
}
