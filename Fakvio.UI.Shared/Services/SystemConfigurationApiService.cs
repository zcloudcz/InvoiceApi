using Fakvio.Contracts.Dto.SystemConfiguration;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor API client for system configuration (SMTP + JWT settings).
/// Calls the /api/system-configuration endpoints on the backend.
/// SysAdmin-only — used by the SystemSettings.razor page.
/// </summary>
public class SystemConfigurationApiService : ApiClientBase
{
    public SystemConfigurationApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<SystemConfigurationApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Gets the current system configuration from the API.
    /// </summary>
    public async Task<SystemConfigurationDto?> GetAsync()
        => await GetAsync<SystemConfigurationDto>("/api/system-configuration");

    /// <summary>
    /// Updates the system configuration with new values.
    /// </summary>
    public async Task<SystemConfigurationDto?> UpdateAsync(UpdateSystemConfigurationDto dto)
        => await PutAsync<UpdateSystemConfigurationDto, SystemConfigurationDto>("/api/system-configuration", dto);

    /// <summary>
    /// Tests the currently configured Azure Blob Storage connection.
    /// Always returns a result — never throws on a connection failure (failure is in result.Success = false).
    /// Throws ApiException only on HTTP-level errors (auth, server crash, etc.).
    /// </summary>
    public async Task<BlobConnectionTestResultDto?> TestBlobConnectionAsync()
        => await PostWithoutBodyAsync<BlobConnectionTestResultDto>("/api/system-configuration/test-blob-connection");
}
