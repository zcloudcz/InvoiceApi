using InvoiceApi.Application.Dto.ContentTemplate;
using InvoiceApi.Domain.Enums;

namespace InvoiceApi.Application.Service;

/// <summary>
/// Service interface for managing content templates (unified PDF + email templates).
/// Provides CRUD operations, default template lookup, and Handlebars placeholder rendering.
/// Replaces the old IEmailTemplateService by adding PDF template support.
/// </summary>
public interface IContentTemplateService
{
    /// <summary>
    /// Gets all content templates, optionally including inactive ones.
    /// </summary>
    Task<List<ContentTemplateDto>> GetAllAsync(bool includeInactive = false, CancellationToken ct = default);

    /// <summary>
    /// Gets all content templates filtered by type.
    /// </summary>
    Task<List<ContentTemplateDto>> GetAllByTypeAsync(EContentTemplateType templateType, bool includeInactive = false, CancellationToken ct = default);

    /// <summary>
    /// Gets a single content template by its ID.
    /// </summary>
    Task<ContentTemplateDto?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Gets the default template for a given template type.
    /// Returns null if no default is configured for that type.
    /// </summary>
    Task<ContentTemplateDto?> GetDefaultByTypeAsync(EContentTemplateType templateType, CancellationToken ct = default);

    /// <summary>
    /// Creates a new content template.
    /// If marked as default, any existing default for the same type is unset.
    /// </summary>
    Task<ContentTemplateDto> CreateAsync(CreateContentTemplateDto createDto, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing content template (partial update — only non-null fields are changed).
    /// </summary>
    Task<ContentTemplateDto?> UpdateAsync(long id, UpdateContentTemplateDto updateDto, CancellationToken ct = default);

    /// <summary>
    /// Deletes a content template (soft delete — sets IsActive = false).
    /// </summary>
    Task<bool> DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Renders a template by substituting Handlebars placeholders with actual values.
    /// Returns a tuple of (Subject, HtmlBody) with all {{Key}} placeholders replaced.
    /// Subject will be null for PDF templates.
    /// </summary>
    /// <param name="templateId">Template ID to render</param>
    /// <param name="placeholders">Key-value pairs for placeholder substitution</param>
    /// <param name="ct">Cancellation token</param>
    Task<(string? Subject, string HtmlBody)> RenderTemplateAsync(
        long templateId,
        Dictionary<string, string> placeholders,
        CancellationToken ct = default);
}
