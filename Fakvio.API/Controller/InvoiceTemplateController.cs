using System.ComponentModel.DataAnnotations;
using Fakvio.Application.Exceptions;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.InvoiceTemplate;
using Fakvio.Application.Service;
using Fakvio.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for invoice template management
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class InvoiceTemplateController : ControllerBase
{
    private readonly IInvoiceTemplateService _templateService;
    private readonly ILogger<InvoiceTemplateController> _logger;

    public InvoiceTemplateController(
        IInvoiceTemplateService templateService,
        ILogger<InvoiceTemplateController> logger)
    {
        _templateService = templateService;
        _logger = logger;
    }

    /// <summary>
    /// Get all active templates
    /// Used for template selection in UI
    /// </summary>
    [HttpGet("active")]
    public async Task<ActionResult<List<InvoiceTemplateDto>>> GetActiveTemplates(
        [FromQuery] EDocumentType? documentType = null,
        [FromQuery] string? category = null,
        CancellationToken cancellationToken = default)
    {
        var templates = await _templateService.GetActiveTemplatesAsync(documentType, category, cancellationToken);
        return Ok(templates);
    }

    /// <summary>
    /// Get templates with pagination, filtering, and sorting
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetTemplatesPaged(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? search = null,
        [FromQuery] string? name = null,
        [FromQuery] EDocumentType? documentType = null,
        [FromQuery] string? category = null,
        [FromQuery] bool? isActive = null,
        [FromQuery] long? issuerId = null,
        [FromQuery] string sortBy = "Name",
        [FromQuery] bool isDescending = false,
        [FromQuery] DateTime? lastUsedAtFrom = null,
        [FromQuery] DateTime? lastUsedAtTo = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _templateService.GetTemplatesPagedAsync(
            page, pageSize, search, name, documentType, category, isActive, issuerId, sortBy, isDescending,
            lastUsedAtFrom, lastUsedAtTo, cancellationToken);

        return Ok(result);
    }

    /// <summary>
    /// Get template by ID
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<InvoiceTemplateDto>> GetTemplateById(long id, CancellationToken cancellationToken)
    {
        var template = await _templateService.GetTemplateByIdAsync(id, cancellationToken);

        if (template == null)
            return NotFound($"Template with ID {id} not found");

        return Ok(template);
    }

    /// <summary>
    /// Create a new invoice template
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<InvoiceTemplateDto>> CreateTemplate(
        [FromBody] CreateInvoiceTemplateDto createDto,
        CancellationToken cancellationToken)
    {
        try
        {
            var template = await _templateService.CreateTemplateAsync(createDto, cancellationToken);
            return CreatedAtAction(nameof(GetTemplateById), new { id = template.Id }, template);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Update an existing invoice template
    /// </summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<InvoiceTemplateDto>> UpdateTemplate(
        long id,
        [FromBody] UpdateInvoiceTemplateDto updateDto,
        CancellationToken cancellationToken)
    {
        try
        {
            var template = await _templateService.UpdateTemplateAsync(id, updateDto, cancellationToken);

            if (template == null)
                return NotFound($"Template with ID {id} not found");

            return Ok(template);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Delete (soft delete) an invoice template
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteTemplate(long id, CancellationToken cancellationToken)
    {
        var deleted = await _templateService.DeleteTemplateAsync(id, cancellationToken);

        if (!deleted)
            return NotFound($"Template with ID {id} not found");

        return NoContent();
    }

    /// <summary>
    /// Create an invoice from a template
    /// Copies template data and allows client-specific customization
    /// </summary>
    [HttpPost("{id}/create-invoice")]
    public async Task<ActionResult<InvoiceDto>> CreateInvoiceFromTemplate(
        long id,
        [FromBody] CreateInvoiceFromTemplateDto createDto,
        CancellationToken cancellationToken)
    {
        try
        {
            var invoice = await _templateService.CreateInvoiceFromTemplateAsync(id, createDto, cancellationToken);
            return CreatedAtAction(
                actionName: "GetInvoiceById",
                controllerName: "Invoice",
                routeValues: new { id = invoice.Id },
                value: invoice);
        }
        catch (TenantNotReadyException ex)
        {
            // Only reachable with AutoComplete = true: creating the draft is always allowed,
            // issuing it is not. Same 400 shape as POST /api/invoice/{id}/complete.
            _logger.LogWarning(
                "Cannot auto-complete invoice from template {TemplateId} — tenant not ready: {MissingFields}",
                id, string.Join(", ", ex.MissingFields));

            return BadRequest(new
            {
                code          = ex.Code,
                message       = ex.Message,
                missingFields = ex.MissingFields,
                issues        = ex.Issues
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Create a template from an existing invoice
    /// Useful for saving frequently used invoice configurations
    /// </summary>
    [HttpPost("from-invoice/{invoiceId}")]
    public async Task<ActionResult<InvoiceTemplateDto>> CreateTemplateFromInvoice(
        long invoiceId,
        [FromBody] CreateTemplateFromInvoiceRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var template = await _templateService.CreateTemplateFromInvoiceAsync(
                invoiceId,
                request.TemplateName,
                request.Description,
                request.Category,
                cancellationToken);

            return CreatedAtAction(nameof(GetTemplateById), new { id = template.Id }, template);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Request body for creating a template from an invoice
    /// </summary>
    public class CreateTemplateFromInvoiceRequest
    {
        /// <summary>
        /// Template name
        /// </summary>
        [Required]
        [StringLength(200)]
        public string TemplateName { get; set; } = string.Empty;

        /// <summary>
        /// Optional description
        /// </summary>
        [StringLength(2000)]
        public string? Description { get; set; }

        /// <summary>
        /// Optional category
        /// </summary>
        [StringLength(100)]
        public string? Category { get; set; }
    }
}
