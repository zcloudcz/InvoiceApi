using Fakvio.Contracts.Dto.ContentTemplate;
using Fakvio.Domain.Enums;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// API client for the unified Content Template system.
/// Consumes endpoints from ContentTemplateController.
/// Handles both PDF templates and email templates through a single service.
/// </summary>
public class ContentTemplateApiService : ApiClientBase
{
    public ContentTemplateApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<ContentTemplateApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Gets all content templates, optionally filtered by type.
    /// GET /api/contenttemplate
    /// </summary>
    public async Task<List<ContentTemplateDto>> GetAllAsync(
        EContentTemplateType? templateType = null, bool includeInactive = false)
    {
        try
        {
            var url = $"/api/contenttemplate?includeInactive={includeInactive}";
            if (templateType.HasValue)
                url += $"&templateType={templateType.Value}";

            return await GetAsync<List<ContentTemplateDto>>(url) ?? [];
        }
        catch (ApiException)
        {
            // Graceful degradation for list endpoints — show empty grid instead of crashing.
            // 401 is already handled by UnauthorizedRedirectHandler (redirects to /login).
            return [];
        }
    }

    /// <summary>
    /// Gets a specific content template by ID.
    /// GET /api/contenttemplate/{id}
    /// </summary>
    public async Task<ContentTemplateDto?> GetByIdAsync(long id)
    {
        return await GetAsync<ContentTemplateDto>($"/api/contenttemplate/{id}");
    }

    /// <summary>
    /// Gets the default template for a given template type.
    /// GET /api/contenttemplate/default/{templateType}
    /// </summary>
    public async Task<ContentTemplateDto?> GetDefaultByTypeAsync(EContentTemplateType templateType)
    {
        return await GetAsync<ContentTemplateDto>($"/api/contenttemplate/default/{templateType}");
    }

    /// <summary>
    /// Creates a new content template.
    /// POST /api/contenttemplate
    /// </summary>
    public async Task<ContentTemplateDto?> CreateAsync(CreateContentTemplateDto createDto)
    {
        return await PostAsync<CreateContentTemplateDto, ContentTemplateDto>("/api/contenttemplate", createDto);
    }

    /// <summary>
    /// Updates an existing content template.
    /// PUT /api/contenttemplate/{id}
    /// </summary>
    public async Task<ContentTemplateDto?> UpdateAsync(long id, UpdateContentTemplateDto updateDto)
    {
        return await PutAsync<UpdateContentTemplateDto, ContentTemplateDto>($"/api/contenttemplate/{id}", updateDto);
    }

    /// <summary>
    /// Deletes a content template (soft delete).
    /// DELETE /api/contenttemplate/{id}
    /// </summary>
    public async Task<bool> DeleteAsync(long id)
    {
        return await base.DeleteAsync($"/api/contenttemplate/{id}");
    }

    /// <summary>
    /// Previews a content template with real or sample data.
    /// - PDF templates: returns PDF bytes (rendered with first valid invoice).
    /// - Email templates: returns rendered HTML string.
    /// GET /api/contenttemplate/{id}/preview
    /// </summary>
    public async Task<byte[]?> PreviewPdfAsync(long id)
    {
        return await GetBytesAsync($"/api/contenttemplate/{id}/preview");
    }

    /// <summary>
    /// Previews an email template — returns rendered HTML string.
    /// GET /api/contenttemplate/{id}/preview (Content-Type: text/html)
    /// </summary>
    public async Task<string?> PreviewHtmlAsync(long id)
    {
        return await GetStringAsync($"/api/contenttemplate/{id}/preview");
    }
}
