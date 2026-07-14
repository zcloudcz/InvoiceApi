using Fakvio.Contracts.Dto.User;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor service for the current user's UI preferences (GET/PUT /api/user-preferences).
/// Inherits ApiClientBase for shared auth, logging, and error handling.
/// </summary>
public class UserPreferencesApiService : ApiClientBase
{
    public UserPreferencesApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<UserPreferencesApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Gets the current user's preferences (server returns defaults when never saved).
    /// Returns defaults on API failure so the UI never breaks because of preferences.
    /// </summary>
    public async Task<UserPreferencesDto> GetAsync()
    {
        try
        {
            return await GetAsync<UserPreferencesDto>("/api/user-preferences")
                   ?? new UserPreferencesDto();
        }
        catch (ApiException)
        {
            return new UserPreferencesDto();
        }
    }

    /// <summary>
    /// Saves the current user's preferences. Returns the saved values, or null on failure.
    /// </summary>
    public async Task<UserPreferencesDto?> UpdateAsync(UserPreferencesDto dto)
    {
        return await PutAsync<UserPreferencesDto, UserPreferencesDto>("/api/user-preferences", dto);
    }
}
