using Fakvio.Contracts.Dto.ContentTemplate;
using Fakvio.Application.Service;
using Fakvio.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

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
    private readonly IInvoiceService _invoiceService;
    private readonly IPdfExportService _pdfExportService;
    private readonly IClientService _clientService;
    private readonly ILogger<ContentTemplateController> _logger;

    public ContentTemplateController(
        IContentTemplateService contentTemplateService,
        IInvoiceService invoiceService,
        IPdfExportService pdfExportService,
        IClientService clientService,
        ILogger<ContentTemplateController> logger)
    {
        _contentTemplateService = contentTemplateService;
        _invoiceService = invoiceService;
        _pdfExportService = pdfExportService;
        _clientService = clientService;
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

    /// <summary>
    /// Generates a preview of the template with real or sample data.
    /// - PDF templates (InvoicePdf, CreditNotePdf): finds the first valid invoice/credit note
    ///   of the matching document type and generates a PDF using this template.
    /// - Email templates: renders placeholders with sample data from the issuer company
    ///   and returns the rendered HTML string (Content-Type: text/html).
    /// </summary>
    /// <param name="id">Content template ID to preview</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>PDF file for document templates, or rendered HTML for email templates</returns>
    [HttpGet("{id}/preview")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Preview(
        long id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/contenttemplate/{Id}/preview", id);

        // Load the template to determine its type
        var template = await _contentTemplateService.GetByIdAsync(id, cancellationToken);
        if (template == null)
            return NotFound(new { message = $"Content template with ID {id} not found" });

        // --- PDF document templates: render with first valid invoice ---
        if (template.TemplateType is EContentTemplateType.InvoicePdf
            or EContentTemplateType.CreditNotePdf)
        {
            // Determine which document type to look for
            var docType = template.TemplateType == EContentTemplateType.CreditNotePdf
                ? EDocumentType.CreditNote
                : EDocumentType.Invoice;

            // Find the first valid invoice of the matching document type
            var invoices = await _invoiceService.GetAllInvoicesAsync(docType, null, null, null, cancellationToken);
            var invoice = invoices.FirstOrDefault();

            if (invoice == null)
                return BadRequest(new { message = $"No {docType} found in the system to preview this template." });

            // Generate PDF using the specific template
            var pdfBytes = await _pdfExportService.GenerateInvoicePdfAsync(
                invoice.Id, id, cancellationToken);

            return File(pdfBytes, "application/pdf", $"Preview_{template.Name}.pdf");
        }

        // --- Email templates: render with sample placeholder data ---
        // Build sample placeholders from the issuer (user's company) data
        var placeholders = await BuildEmailPreviewPlaceholdersAsync(cancellationToken);

        var (subject, htmlBody) = await _contentTemplateService.RenderTemplateAsync(
            id, placeholders, cancellationToken);

        // Return rendered HTML as content (not JSON)
        return Content(htmlBody, "text/html", System.Text.Encoding.UTF8);
    }

    /// <summary>
    /// Builds a dictionary of sample placeholder values for email template preview.
    /// Uses the issuer company data (from JWT context) and sample invoice-like values.
    /// </summary>
    private async Task<Dictionary<string, string>> BuildEmailPreviewPlaceholdersAsync(
        CancellationToken ct)
    {
        // Try to load the issuer company for realistic preview data
        string companyName = "Acme s.r.o.";
        string fullName = "Jan Novák";
        try
        {
            var issuer = await _clientService.GetIssuerAsync(ct);
            if (issuer != null)
            {
                companyName = issuer.CompanyName ?? companyName;
            }
        }
        catch
        {
            // Non-critical — use defaults if issuer lookup fails
        }

        // Sample data that covers all known email template placeholders
        return new Dictionary<string, string>
        {
            ["InvoiceNumber"] = "FV-2026-0001",
            ["CompanyName"] = companyName,
            ["TotalWithVat"] = "12 100,00",
            ["CurrencyCode"] = "CZK",
            ["DueDate"] = DateTime.Now.AddDays(14).ToString("dd.MM.yyyy"),
            ["IssueDate"] = DateTime.Now.ToString("dd.MM.yyyy"),
            ["FullName"] = fullName,
            ["InvitationLink"] = "https://app.fakvio.cz/invite/sample-token",
            ["AppName"] = "Fakvio",
            ["ResetLink"] = "https://app.fakvio.cz/reset-password/sample-token",
            ["VerificationCode"] = "123456",
            ["UserEmail"] = "jan.novak@example.com"
        };
    }
}
