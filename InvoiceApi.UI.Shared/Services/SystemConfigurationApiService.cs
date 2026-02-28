using InvoiceApi.Contracts.Dto.SystemConfiguration;
using Microsoft.AspNetCore.Components.Authorization;

namespace InvoiceApi.UI.Shared.Services;

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
}
