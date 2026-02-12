using InvoiceApi.Application.Dto.ContentTemplate;
using InvoiceApi.Application.Service;
using InvoiceApi.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceApi.API.Controller;

/// <summary>
/// Controller for managing content templates (unified PDF + email templates).
/// Provides CRUD operations for creating, reading, updating, and deleting
/// reusable content templates with Handlebars-style placeholders.
/// All users can read templates; only Admin/SysAdmin can modify.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize]
public class ContentTemplateController : ControllerBase
{
    private readonly IContentTemplateService _contentTemplateService;
    private readonly ILogger<ContentTemplateController> _logger;

    public ContentTemplateController(
        IContentTemplateService contentTemplateService,
        ILogger<ContentTemplateController> logger)
    {
        _contentTemplateService = contentTemplateService;
        _logger = logger;
    }

    /// <summary>
    /// Gets all content templates, optionally filtered by type.
    /// </summary>
    /// <param name="templateType">Filter by template type (optional)</param>
    /// <param name="includeInactive">Include inactive templates in results</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [HttpGet]
    [ProducesResponseType(typeof(List<ContentTemplateDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ContentTemplateDto>>> GetAll(
        [FromQuery] EContentTemplateType? templateType = null,
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/contenttemplate - type: {Type}, includeInactive: {IncludeInactive}",
            templateType, includeInactive);

        List<ContentTemplateDto> templates;
        if (templateType.HasValue)
        {
            templates = await _contentTemplateService.GetAllByTypeAsync(templateType.Value, includeInactive, cancellationToken);
        }
        else
        {
            templates = await _contentTemplateService.GetAllAsync(includeInactive, cancellationToken);
        }

        return Ok(templates);
    }

    /// <summary>
    /// Gets a specific content template by ID.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ContentTemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ContentTemplateDto>> GetById(
        long id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/contenttemplate/{Id}", id);

        var template = await _contentTemplateService.GetByIdAsync(id, cancellationToken);
        if (template == null)
        {
            return NotFound(new { message = $"Content template with ID {id} not found" });
        }

        return Ok(template);
    }

    /// <summary>
    /// Gets the default template for a given template type.
    /// </summary>
    [HttpGet("default/{templateType}")]
    [ProducesResponseType(typeof(ContentTemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ContentTemplateDto>> GetDefaultByType(
        EContentTemplateType templateType,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/contenttemplate/default/{TemplateType}", templateType);

        var template = await _contentTemplateService.GetDefaultByTypeAsync(templateType, cancellationToken);
        if (template == null)
        {
            return NotFound(new { message = $"No default content template found for type {templateType}" });
        }

        return Ok(template);
    }

    /// <summary>
    /// Creates a new content template.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Admin,SysAdmin")]
    [ProducesResponseType(typeof(ContentTemplateDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ContentTemplateDto>> Create(
        [FromBody] CreateContentTemplateDto createDto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("POST /api/contenttemplate - Creating: {Name}, type: {Type}",
            createDto.Name, createDto.TemplateType);

        try
        {
            var template = await _contentTemplateService.CreateAsync(createDto, cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = template.Id }, template);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Failed to create content template: {Message}", ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Updates an existing content template.
    /// </summary>
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin,SysAdmin")]
    [ProducesResponseType(typeof(ContentTemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ContentTemplateDto>> Update(
        long id,
        [FromBody] UpdateContentTemplateDto updateDto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("PUT /api/contenttemplate/{Id}", id);

        try
        {
            var template = await _contentTemplateService.UpdateAsync(id, updateDto, cancellationToken);
            if (template == null)
            {
                return NotFound(new { message = $"Content template with ID {id} not found" });
            }

            return Ok(template);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Failed to update content template {Id}: {Message}", id, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Deletes a content template (soft delete — sets IsActive = false).
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin,SysAdmin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        long id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("DELETE /api/contenttemplate/{Id}", id);

        var deleted = await _contentTemplateService.DeleteAsync(id, cancellationToken);
        if (!deleted)
        {
            return NotFound(new { message = $"Content template with ID {id} not found" });
        }

        return NoContent();
    }
}
