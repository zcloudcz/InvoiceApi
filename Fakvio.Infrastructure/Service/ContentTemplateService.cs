using Fakvio.Contracts.Dto.ContentTemplate;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Service for managing content templates in the database.
/// Handles CRUD operations and Handlebars-style placeholder substitution.
/// Replaces the old EmailTemplateService by supporting both PDF and email templates.
/// Placeholders use the format {{PlaceholderName}} and are replaced at render time.
///
/// Dual-context: uses TenantDbContext when a tenant is available (regular users
/// or impersonating SysAdmin), and falls back to MasterDbContext when SysAdmin
/// operates without impersonation or when no tenant context exists (e.g., during
/// invitation emails sent before tenant provisioning completes).
/// </summary>
public partial class ContentTemplateService : IContentTemplateService
{
    private readonly TenantDbContext _tenantContext;
    private readonly MasterDbContext _masterContext;
    private readonly ITenantResolver _tenantResolver;
    private readonly ILogger<ContentTemplateService> _logger;

    public ContentTemplateService(
        TenantDbContext tenantContext,
        MasterDbContext masterContext,
        ITenantResolver tenantResolver,
        ILogger<ContentTemplateService> logger)
    {
        _tenantContext = tenantContext;
        _masterContext = masterContext;
        _tenantResolver = tenantResolver;
        _logger = logger;
    }

    /// <summary>
    /// Whether we're operating in master context (SysAdmin without impersonation).
    /// </summary>
    private bool IsMasterContext => !_tenantResolver.GetCurrentCompanyId().HasValue;

    /// <summary>
    /// Resolves the correct ContentTemplate DbSet based on context.
    /// </summary>
    private DbSet<ContentTemplate> TemplateSet => IsMasterContext ? _masterContext.ContentTemplate : _tenantContext.ContentTemplate;

    /// <summary>
    /// Resolves the correct DbContext for SaveChanges operations.
    /// </summary>
    private DbContext ActiveContext => IsMasterContext ? _masterContext : _tenantContext;

    /// <inheritdoc />
    public async Task<List<ContentTemplateDto>> GetAllAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        _logger.LogInformation("Getting all content templates, includeInactive: {IncludeInactive}", includeInactive);

        var query = TemplateSet.AsNoTracking();

        if (!includeInactive)
        {
            query = query.Where(t => t.IsActive);
        }

        var templates = await query
            .OrderBy(t => t.TemplateType)
            .ThenBy(t => t.Name)
            .ToListAsync(ct);

        return templates.Select(t => t.ToContentTemplateDto()).ToList();
    }

    /// <inheritdoc />
    public async Task<List<ContentTemplateDto>> GetAllByTypeAsync(
        EContentTemplateType templateType, bool includeInactive = false, CancellationToken ct = default)
    {
        _logger.LogInformation("Getting content templates for type: {TemplateType}", templateType);

        var query = TemplateSet
            .AsNoTracking()
            .Where(t => t.TemplateType == templateType);

        if (!includeInactive)
        {
            query = query.Where(t => t.IsActive);
        }

        var templates = await query
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

        return templates.Select(t => t.ToContentTemplateDto()).ToList();
    }

    /// <inheritdoc />
    public async Task<ContentTemplateDto?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        _logger.LogInformation("Getting content template by ID: {Id}", id);

        var template = await TemplateSet
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, ct);

        return template?.ToContentTemplateDto();
    }

    /// <inheritdoc />
    public async Task<ContentTemplateDto?> GetDefaultByTypeAsync(EContentTemplateType templateType, CancellationToken ct = default)
    {
        _logger.LogInformation("Getting default content template for type: {TemplateType} (any language)", templateType);

        // OrderBy(Id): deterministic ordering — EF warns when FirstOrDefault has no OrderBy
        // and the predicate could match multiple rows (e.g., multiple default templates).
        var template = await TemplateSet
            .AsNoTracking()
            .Where(t => t.TemplateType == templateType && t.IsDefault && t.IsActive)
            .OrderBy(t => t.Id)
            .FirstOrDefaultAsync(ct);

        return template?.ToContentTemplateDto();
    }

    /// <inheritdoc />
    public async Task<ContentTemplateDto?> GetDefaultByTypeAsync(
        EContentTemplateType templateType, string language, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Getting default content template for type: {TemplateType}, language: {Language}",
            templateType, language);

        // Step 1: Try to find an exact match — same type + same language + default + active.
        var template = await TemplateSet
            .AsNoTracking()
            .Where(t => t.TemplateType == templateType
                        && t.Language == language
                        && t.IsDefault
                        && t.IsActive)
            .OrderBy(t => t.Id)
            .FirstOrDefaultAsync(ct);

        if (template != null)
        {
            _logger.LogInformation(
                "Found language-matched template '{Name}' (ID {Id}) for type {Type}, language {Lang}",
                template.Name, template.Id, templateType, language);
            return template.ToContentTemplateDto();
        }

        // Step 2: Fallback — any-language default for this type.
        // This ensures documents are still generated even if no template exists in the requested language.
        _logger.LogInformation(
            "No template found for language '{Language}', falling back to any-language default for type {Type}",
            language, templateType);

        return await GetDefaultByTypeAsync(templateType, ct);
    }

    /// <inheritdoc />
    public async Task<ContentTemplateDto> CreateAsync(CreateContentTemplateDto createDto, CancellationToken ct = default)
    {
        _logger.LogInformation("Creating content template: {Name}, type: {Type}", createDto.Name, createDto.TemplateType);

        var entity = new ContentTemplate
        {
            Name = createDto.Name,
            Subject = createDto.Subject,
            HtmlBody = createDto.HtmlBody,
            TemplateType = createDto.TemplateType,
            IsDefault = createDto.IsDefault,
            IsActive = true,
            Description = createDto.Description,
            Language = createDto.Language
        };

        // If this template is marked as default, unset any existing default
        // for the same type AND language — so each (type, language) has at most one default.
        if (entity.IsDefault)
        {
            await UnsetDefaultForTypeAsync(entity.TemplateType, entity.Language, ct);
        }

        TemplateSet.Add(entity);
        await ActiveContext.SaveChangesAsync(ct);

        _logger.LogInformation("Content template created with ID: {Id}", entity.Id);
        return entity.ToContentTemplateDto();
    }

    /// <inheritdoc />
    public async Task<ContentTemplateDto?> UpdateAsync(long id, UpdateContentTemplateDto updateDto, CancellationToken ct = default)
    {
        _logger.LogInformation("Updating content template {Id}", id);

        var entity = await TemplateSet.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (entity == null)
        {
            _logger.LogWarning("Content template {Id} not found", id);
            return null;
        }

        // Apply partial updates — only update fields that are provided
        if (updateDto.Name != null) entity.Name = updateDto.Name;
        if (updateDto.Subject != null) entity.Subject = updateDto.Subject;
        if (updateDto.HtmlBody != null) entity.HtmlBody = updateDto.HtmlBody;
        if (updateDto.TemplateType.HasValue) entity.TemplateType = updateDto.TemplateType.Value;
        if (updateDto.IsActive.HasValue) entity.IsActive = updateDto.IsActive.Value;
        if (updateDto.Description != null) entity.Description = updateDto.Description;
        if (updateDto.Language != null) entity.Language = updateDto.Language;

        // Handle default flag — unset previous defaults when setting a new one.
        // Scoped by (TemplateType, Language) so each language has its own default.
        if (updateDto.IsDefault.HasValue)
        {
            if (updateDto.IsDefault.Value && !entity.IsDefault)
            {
                await UnsetDefaultForTypeAsync(entity.TemplateType, entity.Language, ct);
            }
            entity.IsDefault = updateDto.IsDefault.Value;
        }

        await ActiveContext.SaveChangesAsync(ct);

        _logger.LogInformation("Content template {Id} updated", id);
        return entity.ToContentTemplateDto();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        _logger.LogInformation("Soft-deleting content template {Id}", id);

        var entity = await TemplateSet.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (entity == null)
        {
            _logger.LogWarning("Content template {Id} not found for deletion", id);
            return false;
        }

        // Soft delete — set IsActive to false
        entity.IsActive = false;
        await ActiveContext.SaveChangesAsync(ct);

        _logger.LogInformation("Content template {Id} soft-deleted", id);
        return true;
    }

    /// <inheritdoc />
    public async Task<(string? Subject, string HtmlBody)> RenderTemplateAsync(
        long templateId,
        Dictionary<string, string> placeholders,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Rendering content template {TemplateId} with {Count} placeholders", templateId, placeholders.Count);

        var template = await TemplateSet
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == templateId, ct)
            ?? throw new KeyNotFoundException($"Content template with ID {templateId} not found.");

        // Replace all {{Key}} placeholders with their corresponding values
        var subject = template.Subject != null ? ReplacePlaceholders(template.Subject, placeholders) : null;
        var htmlBody = ReplacePlaceholders(template.HtmlBody, placeholders);

        return (subject, htmlBody);
    }

    /// <summary>
    /// Replaces all Handlebars-style placeholders ({{Key}}) in the input text.
    /// Uses a compiled regex to find all {{...}} patterns and substitutes them with values
    /// from the provided dictionary. Unknown placeholders are left as-is.
    /// </summary>
    private static string ReplacePlaceholders(string input, Dictionary<string, string> placeholders)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        return PlaceholderRegex().Replace(input, match =>
        {
            var key = match.Groups[1].Value.Trim();
            return placeholders.TryGetValue(key, out var value) ? value : match.Value;
        });
    }

    /// <summary>
    /// Unsets the IsDefault flag for all templates of the given type AND language.
    /// Called before setting a new default to ensure only one default per (type, language) pair.
    /// This allows having separate defaults for Czech and English templates of the same type.
    /// </summary>
    private async Task UnsetDefaultForTypeAsync(
        EContentTemplateType templateType, string language, CancellationToken ct)
    {
        var currentDefaults = await TemplateSet
            .Where(t => t.TemplateType == templateType && t.Language == language && t.IsDefault)
            .ToListAsync(ct);

        foreach (var t in currentDefaults)
        {
            t.IsDefault = false;
        }
    }

    /// <summary>
    /// Compiled regex for matching Handlebars placeholders: {{PlaceholderName}}
    /// </summary>
    [GeneratedRegex(@"\{\{(.+?)\}\}")]
    private static partial Regex PlaceholderRegex();
}
