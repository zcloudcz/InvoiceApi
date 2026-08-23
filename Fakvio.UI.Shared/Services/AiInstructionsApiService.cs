using Fakvio.Contracts.Dto.SystemConfiguration;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// API client for the editable AI assistant instructions.
/// Backs the SysAdmin-only /ai-instructions page.
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

    /// <summary>Loads the stored instructions.</summary>
    public async Task<AiInstructionsDto?> GetAsync()
        => await GetAsync<AiInstructionsDto>(BaseUrl);

    /// <summary>Saves a partial update (null = keep the stored value).</summary>
    public async Task<AiInstructionsDto?> UpdateAsync(UpdateAiInstructionsDto dto)
        => await PutAsync<UpdateAiInstructionsDto, AiInstructionsDto>(BaseUrl, dto);

    /// <summary>
    /// Clears both parts. Returns true on success — ApiClientBase.DeleteAsync reports
    /// success as a bool rather than deserializing the response body.
    /// </summary>
    public async Task<bool> ResetToDefaultAsync()
        => await DeleteAsync(BaseUrl);

    /// <summary>Loads the rendered preview of the full system prompt.</summary>
    public async Task<AiInstructionsPreviewDto?> GetPreviewAsync()
        => await GetAsync<AiInstructionsPreviewDto>($"{BaseUrl}/preview");
}
