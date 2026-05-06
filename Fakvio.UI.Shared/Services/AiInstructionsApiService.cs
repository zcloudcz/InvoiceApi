using Fakvio.Contracts.Dto.SystemConfiguration;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor API client for AI assistant system prompt instructions.
/// Calls the /api/system-configuration/ai-instructions endpoints.
/// SysAdmin-only — used by the AiInstructions.razor page.
/// </summary>
public class AiInstructionsApiService : ApiClientBase
{
    private const string BaseUrl = "/api/system-configuration/ai-instructions";

    public AiInstructionsApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<AiInstructionsApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Gets the current custom AI instructions (prompt + appendix) from the API.
    /// </summary>
    public async Task<AiInstructionsDto?> GetAsync()
        => await GetAsync<AiInstructionsDto>(BaseUrl);

    /// <summary>
    /// Updates the AI instructions (partial update — null = keep existing).
    /// </summary>
    public async Task<AiInstructionsDto?> UpdateAsync(UpdateAiInstructionsDto dto)
        => await PutAsync<UpdateAiInstructionsDto, AiInstructionsDto>(BaseUrl, dto);

    /// <summary>
    /// Resets both custom prompt and appendix to hardcoded defaults.
    /// Returns the updated instructions (both fields null) on success, null on failure.
    /// </summary>
    public async Task<bool> ResetToDefaultAsync()
        => await DeleteAsync(BaseUrl);

    /// <summary>
    /// Gets a preview of the full assembled system prompt.
    /// Business context stats are shown as placeholders.
    /// </summary>
    public async Task<AiInstructionsPreviewDto?> GetPreviewAsync()
        => await GetAsync<AiInstructionsPreviewDto>($"{BaseUrl}/preview");
}
