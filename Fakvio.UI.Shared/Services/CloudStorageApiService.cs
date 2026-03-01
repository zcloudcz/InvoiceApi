using Fakvio.Contracts.Dto.CloudStorage;
using Fakvio.Domain.Enums;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor UI service for cloud storage API operations.
/// Communicates with CloudStorageController endpoints via HTTP.
/// Inherits from ApiClientBase for JWT auth + impersonation support.
/// </summary>
public class CloudStorageApiService : ApiClientBase
{
    public CloudStorageApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<CloudStorageApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Gets the connection status of all cloud storage providers for the current company.
    /// </summary>
    public async Task<List<CloudStorageStatusDto>> GetStatusAsync()
    {
        try
        {
            return await GetAsync<List<CloudStorageStatusDto>>("api/cloud-storage/status") ?? [];
        }
        catch (ApiException)
        {
            // Graceful degradation for list endpoints — show empty grid instead of crashing.
            // 401 is already handled by UnauthorizedRedirectHandler (redirects to /login).
            return [];
        }
    }

    /// <summary>
    /// Generates the OAuth authorization URL for a specific provider.
    /// The returned URL should be opened in a popup window.
    /// </summary>
    public async Task<string?> GetAuthorizationUrlAsync(ECloudStorageProvider provider, string redirectUri)
    {
        return await GetAsync<string>(
            $"api/cloud-storage/auth-url?provider={provider}&redirectUri={Uri.EscapeDataString(redirectUri)}");
    }

    /// <summary>
    /// Exchanges an OAuth authorization code for tokens.
    /// Called by the callback page after the user approves access.
    /// </summary>
    public async Task<bool> ExchangeCodeAsync(ECloudStorageProvider provider, string code, string redirectUri)
    {
        return await PostWithoutBodyBoolAsync(
            $"api/cloud-storage/callback?provider={provider}&code={Uri.EscapeDataString(code)}&redirectUri={Uri.EscapeDataString(redirectUri)}");
    }

    /// <summary>
    /// Disconnects a cloud storage provider (revokes tokens, clears credentials).
    /// </summary>
    public async Task<bool> DisconnectAsync(ECloudStorageProvider provider)
    {
        return await PostWithoutBodyBoolAsync($"api/cloud-storage/disconnect?provider={provider}");
    }

    /// <summary>
    /// Lists subfolders in a cloud storage provider.
    /// Pass parentId=null for root-level folders.
    /// </summary>
    public async Task<List<CloudStorageFolderDto>> ListFoldersAsync(
        ECloudStorageProvider provider, string? parentId = null)
    {
        try
        {
            var url = $"api/cloud-storage/folders?provider={provider}";
            if (!string.IsNullOrEmpty(parentId))
                url += $"&parentId={Uri.EscapeDataString(parentId)}";

            return await GetAsync<List<CloudStorageFolderDto>>(url) ?? [];
        }
        catch (ApiException)
        {
            // Graceful degradation for list endpoints — show empty grid instead of crashing.
            // 401 is already handled by UnauthorizedRedirectHandler (redirects to /login).
            return [];
        }
    }

    /// <summary>
    /// Sets the destination folder for a specific provider.
    /// </summary>
    public async Task<bool> SetFolderAsync(
        ECloudStorageProvider provider, CloudStorageSetFolderRequest request)
    {
        return await PostBoolAsync($"api/cloud-storage/set-folder?provider={provider}", request);
    }

    /// <summary>
    /// Tests the connection to a specific cloud storage provider.
    /// </summary>
    public async Task<bool> TestConnectionAsync(ECloudStorageProvider provider)
    {
        return await PostWithoutBodyBoolAsync($"api/cloud-storage/test?provider={provider}");
    }
}
