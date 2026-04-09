using System.IO.Compression;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Email;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Application.Service;
using Fakvio.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for managing invoices and credit notes.
/// Provides CRUD operations and invoice lifecycle management.
/// Users can only manage invoices from their own company (IssuerId = user's CompanyId).
/// SysAdmin can impersonate a company via X-Company-Id header — the middleware
/// sets the CompanyId claim, so filtering works automatically.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize] // All endpoints require authentication
public class InvoiceController : ControllerBase
{
    private readonly IInvoiceService _invoiceService;
    private readonly IPdfExportService _pdfExportService;
    private readonly IEmailService _emailService;
    private readonly IQrPaymentService _qrPaymentService;
    private readonly ICloudStorageOrchestrator _cloudStorageOrchestrator;
    private readonly ILogger<InvoiceController> _logger;

    public InvoiceController(
        IInvoiceService invoiceService,
        IPdfExportService pdfExportService,
        IEmailService emailService,
        IQrPaymentService qrPaymentService,
        ICloudStorageOrchestrator cloudStorageOrchestrator,
        ILogger<InvoiceController> logger)
    {
        _invoiceService = invoiceService;
        _pdfExportService = pdfExportService;
        _emailService = emailService;
        _qrPaymentService = qrPaymentService;
        _cloudStorageOrchestrator = cloudStorageOrchestrator;
        _logger = logger;
    }

    /// <summary>
    /// Gets the effective company ID from JWT claims.
    /// For regular users, this is their own CompanyId from JWT.
    /// For SysAdmin impersonating, the ImpersonationMiddleware sets this from X-Company-Id header.
    /// Returns null if no company context (SysAdmin not impersonating).
    /// </summary>
    private long? GetCurrentUserCompanyId()
    {
        var companyIdClaim = User.FindFirst("CompanyId")?.Value;
        return long.TryParse(companyIdClaim, out var companyId) ? companyId : null;
    }

    /// <summary>
    /// Gets all invoices and credit notes with optional filtering
    /// </summary>
    /// <param name="documentType">Filter by document type (Invoice or CreditNote)</param>
    /// <param name="status">Filter by status (Draft, Completed, Paid, etc.)</param>
    /// <param name="clientId">Filter by client ID</param>
    /// <param name="issuerId">Filter by issuer ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of invoices/credit notes</returns>
    /// <response code="200">Returns list of invoices</response>
    [HttpGet]
    [ProducesResponseType(typeof(List<InvoiceDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<InvoiceDto>>> GetAllInvoices(
        [FromQuery] EDocumentType? documentType = null,
        [FromQuery] EInvoiceStatus? status = null,
        [FromQuery] long? clientId = null,
        [FromQuery] long? issuerId = null,
        CancellationToken cancellationToken = default)
    {
        // Auto-filter by company: regular users see only their company's invoices,
        // SysAdmin sees only impersonated company's invoices (or all if not impersonating)
        var companyId = GetCurrentUserCompanyId();
        if (companyId.HasValue)
        {
            issuerId = companyId.Value;
        }

        _logger.LogInformation("GET /api/invoice - documentType: {DocumentType}, status: {Status}, clientId: {ClientId}, issuerId: {IssuerId}",
            documentType, status, clientId, issuerId);

        var invoices = await _invoiceService.GetAllInvoicesAsync(documentType, status, clientId, issuerId, cancellationToken);
        return Ok(invoices);
    }

    /// <summary>
    /// Gets paginated, filtered and sorted invoices/credit notes
    /// Supports pagination, filtering by multiple criteria, and sorting
    /// </summary>
    /// <param name="filter">Filter parameters including pagination, search, and sorting</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Paged result of invoices</returns>
    /// <response code="200">Returns paged list of invoices</response>
    [HttpGet("paged")]
    [ProducesResponseType(typeof(PagedResult<InvoiceDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<InvoiceDto>>> GetInvoicesPaged(
        [FromQuery] InvoiceFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        // Auto-filter by company: ensure users only see invoices from their company
        var companyId = GetCurrentUserCompanyId();
        if (companyId.HasValue)
        {
            filter.IssuerId = companyId.Value;
        }

        _logger.LogInformation("GET /api/invoice/paged - Page: {Page}, PageSize: {PageSize}, Search: {Search}, IssuerId: {IssuerId}",
            filter.Page, filter.PageSize, filter.Search, filter.IssuerId);

        var result = await _invoiceService.GetInvoicesPagedAsync(filter, cancellationToken);

        return Ok(result);
    }

    /// <summary>
    /// Gets a specific invoice by ID
    /// </summary>
    /// <param name="id">Invoice ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Invoice data</returns>
    /// <response code="200">Returns the invoice</response>
    /// <response code="404">Invoice not found</response>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(InvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InvoiceDto>> GetInvoiceById(
        long id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/invoice/{Id}", id);

        var invoice = await _invoiceService.GetInvoiceByIdAsync(id, cancellationToken);

        if (invoice == null)
        {
            _logger.LogWarning("Invoice {Id} not found", id);
            return NotFound(new { message = $"Invoice with ID {id} not found" });
        }

        return Ok(invoice);
    }

    /// <summary>
    /// Gets invoice by document number
    /// </summary>
    /// <param name="documentNumber">Document number (e.g., "INV2025001")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Invoice data</returns>
    /// <response code="200">Returns the invoice</response>
    /// <response code="404">Invoice not found</response>
    [HttpGet("by-number/{documentNumber}")]
    [ProducesResponseType(typeof(InvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InvoiceDto>> GetInvoiceByDocumentNumber(
        string documentNumber,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/invoice/by-number/{DocumentNumber}", documentNumber);

        var invoice = await _invoiceService.GetInvoiceByDocumentNumberAsync(documentNumber, cancellationToken);

        if (invoice == null)
        {
            _logger.LogWarning("Invoice with document number {DocumentNumber} not found", documentNumber);
            return NotFound(new { message = $"Invoice with document number {documentNumber} not found" });
        }

        return Ok(invoice);
    }

    /// <summary>
    /// Creates a new invoice or credit note
    /// </summary>
    /// <param name="createDto">Invoice data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created invoice</returns>
    /// <response code="201">Invoice created successfully</response>
    /// <response code="400">Invalid request data</response>
    [HttpPost]
    [ProducesResponseType(typeof(InvoiceDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<InvoiceDto>> CreateInvoice(
        [FromBody] CreateInvoiceDto createDto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("POST /api/invoice - Creating {DocumentType}", createDto.DocumentType);

        try
        {
            var invoice = await _invoiceService.CreateInvoiceAsync(createDto, cancellationToken);

            _logger.LogInformation("{DocumentType} created with ID {Id}", invoice.DocumentType, invoice.Id);

            return CreatedAtAction(
                nameof(GetInvoiceById),
                new { id = invoice.Id },
                invoice);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Failed to create invoice: {Message}", ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Updates an existing invoice
    /// Only allowed for Draft and Completed invoices
    /// </summary>
    /// <param name="id">Invoice ID</param>
    /// <param name="updateDto">Updated data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated invoice</returns>
    /// <response code="200">Invoice updated successfully</response>
    /// <response code="404">Invoice not found</response>
    /// <response code="400">Invalid request or invoice cannot be updated</response>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(InvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<InvoiceDto>> UpdateInvoice(
        long id,
        [FromBody] UpdateInvoiceDto updateDto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("PUT /api/invoice/{Id}", id);

        try
        {
            var invoice = await _invoiceService.UpdateInvoiceAsync(id, updateDto, cancellationToken);

            if (invoice == null)
            {
                _logger.LogWarning("Invoice {Id} not found for update", id);
                return NotFound(new { message = $"Invoice with ID {id} not found" });
            }

            _logger.LogInformation("Invoice {Id} updated successfully", id);
            return Ok(invoice);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Cannot update invoice {Id}: {Message}", id, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Marks invoice as completed
    /// Generates document number if not already set
    /// This action cannot be undone
    /// </summary>
    /// <param name="id">Invoice ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated invoice</returns>
    /// <response code="200">Invoice completed successfully</response>
    /// <response code="404">Invoice not found</response>
    /// <response code="400">Invoice cannot be completed</response>
    [HttpPost("{id}/complete")]
    [ProducesResponseType(typeof(InvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<InvoiceDto>> CompleteInvoice(
        long id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("POST /api/invoice/{Id}/complete", id);

        try
        {
            var invoice = await _invoiceService.CompleteInvoiceAsync(id, cancellationToken);

            if (invoice == null)
            {
                _logger.LogWarning("Invoice {Id} not found", id);
                return NotFound(new { message = $"Invoice with ID {id} not found" });
            }

            _logger.LogInformation("Invoice {Id} completed with number {DocumentNumber}", id, invoice.DocumentNumber);

            // Auto-upload PDF to enabled cloud storage providers (non-blocking).
            // Failures are logged as warnings but don't affect the completion response.
            try
            {
                var pdfBytes = await _pdfExportService.GenerateInvoicePdfAsync(id, null, cancellationToken);
                var fileName = $"{invoice.DocumentNumber}.pdf";
                await _cloudStorageOrchestrator.UploadInvoicePdfAsync(id, pdfBytes, fileName, cancellationToken);
            }
            catch (Exception cloudEx)
            {
                _logger.LogWarning(cloudEx, "Cloud storage upload failed for invoice {Id} (non-blocking)", id);
            }

            return Ok(invoice);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Cannot complete invoice {Id}: {Message}", id, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Marks invoice as paid
    /// Only possible for Completed invoices
    /// </summary>
    /// <param name="id">Invoice ID</param>
    /// <param name="paidAt">Payment date (optional, defaults to now)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated invoice</returns>
    /// <response code="200">Invoice marked as paid successfully</response>
    /// <response code="404">Invoice not found</response>
    /// <response code="400">Invoice cannot be marked as paid</response>
    [HttpPost("{id}/mark-paid")]
    [ProducesResponseType(typeof(InvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<InvoiceDto>> MarkAsPaid(
        long id,
        [FromQuery] DateTime? paidAt = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("POST /api/invoice/{Id}/mark-paid - paidAt: {PaidAt}", id, paidAt);

        try
        {
            var invoice = await _invoiceService.MarkAsPaidAsync(id, paidAt, cancellationToken);

            if (invoice == null)
            {
                _logger.LogWarning("Invoice {Id} not found", id);
                return NotFound(new { message = $"Invoice with ID {id} not found" });
            }

            _logger.LogInformation("Invoice {Id} marked as paid", id);
            return Ok(invoice);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Cannot mark invoice {Id} as paid: {Message}", id, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Deletes an invoice (soft delete)
    /// Only allowed for Draft invoices
    /// </summary>
    /// <param name="id">Invoice ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>No content</returns>
    /// <response code="204">Invoice deleted successfully</response>
    /// <response code="404">Invoice not found</response>
    /// <response code="400">Invoice cannot be deleted</response>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeleteInvoice(
        long id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("DELETE /api/invoice/{Id}", id);

        try
        {
            var deleted = await _invoiceService.DeleteInvoiceAsync(id, cancellationToken);

            if (!deleted)
            {
                _logger.LogWarning("Invoice {Id} not found for deletion", id);
                return NotFound(new { message = $"Invoice with ID {id} not found" });
            }

            _logger.LogInformation("Invoice {Id} deleted successfully", id);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Cannot delete invoice {Id}: {Message}", id, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Restores a soft-deleted invoice back to Draft status.
    /// Only allowed for Deleted invoices — other statuses return 400 Bad Request.
    /// This lets users undo an accidental delete without creating a new invoice.
    /// </summary>
    /// <param name="id">Invoice ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <response code="200">Invoice restored to Draft</response>
    /// <response code="404">Invoice not found</response>
    /// <response code="400">Invoice cannot be restored (not in Deleted status)</response>
    [HttpPost("{id}/restore")]
    [ProducesResponseType(typeof(InvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<InvoiceDto>> RestoreInvoice(
        long id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("POST /api/invoice/{Id}/restore", id);

        try
        {
            var invoice = await _invoiceService.RestoreInvoiceAsync(id, cancellationToken);

            if (invoice == null)
            {
                return NotFound(new { message = $"Invoice with ID {id} not found" });
            }

            _logger.LogInformation("Invoice {Id} restored to Draft", id);
            return Ok(invoice);
        }
        catch (InvalidOperationException ex)
        {
            // This happens when the invoice is not in Deleted status.
            _logger.LogWarning("Cannot restore invoice {Id}: {Message}", id, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Reverts a Completed invoice back to Draft so it can be fully edited.
    /// Only Completed invoices can be reverted — Paid/Creditnoted return 400.
    /// </summary>
    [HttpPost("{id}/revert-to-draft")]
    [ProducesResponseType(typeof(InvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<InvoiceDto>> RevertToDraft(long id, CancellationToken cancellationToken = default)
    {
        try
        {
            var invoice = await _invoiceService.RevertToDraftAsync(id, cancellationToken);
            if (invoice == null)
                return NotFound(new { message = $"Invoice with ID {id} not found" });

            _logger.LogInformation("Invoice {Id} reverted to Draft", id);
            return Ok(invoice);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Cannot revert invoice {Id}: {Message}", id, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Creates a credit note for an existing invoice
    /// Automatically marks the original invoice as creditnoted
    /// </summary>
    /// <param name="invoiceId">Original invoice ID</param>
    /// <param name="createDto">Credit note data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created credit note</returns>
    /// <response code="201">Credit note created successfully</response>
    /// <response code="404">Original invoice not found</response>
    /// <response code="400">Invalid request or cannot create credit note</response>
    [HttpPost("{invoiceId}/credit-note")]
    [ProducesResponseType(typeof(InvoiceDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<InvoiceDto>> CreateCreditNote(
        long invoiceId,
        [FromBody] CreateInvoiceDto createDto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("POST /api/invoice/{InvoiceId}/credit-note", invoiceId);

        try
        {
            var creditNote = await _invoiceService.CreateCreditNoteAsync(invoiceId, createDto, cancellationToken);

            _logger.LogInformation("Credit note created with ID {Id} for invoice {InvoiceId}", creditNote.Id, invoiceId);

            return CreatedAtAction(
                nameof(GetInvoiceById),
                new { id = creditNote.Id },
                creditNote);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Failed to create credit note for invoice {InvoiceId}: {Message}", invoiceId, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Gets all credit notes for a specific invoice
    /// </summary>
    /// <param name="invoiceId">Original invoice ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of credit notes</returns>
    /// <response code="200">Returns list of credit notes</response>
    [HttpGet("{invoiceId}/credit-notes")]
    [ProducesResponseType(typeof(List<InvoiceDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<InvoiceDto>>> GetCreditNotesForInvoice(
        long invoiceId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/invoice/{InvoiceId}/credit-notes", invoiceId);

        var creditNotes = await _invoiceService.GetCreditNotesForInvoiceAsync(invoiceId, cancellationToken);
        return Ok(creditNotes);
    }

    /// <summary>
    /// Generates a PDF document for the specified invoice.
    /// Uses the invoice's HTML template (from DB) or falls back to a default template.
    /// Optionally accepts a templateId query parameter to use a specific content template
    /// instead of the default one for the document type.
    /// Returns the PDF as a downloadable file (application/pdf).
    /// </summary>
    /// <param name="id">Invoice ID to export</param>
    /// <param name="templateId">Optional content template ID — overrides the default PDF template</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>PDF file content</returns>
    /// <response code="200">Returns PDF file</response>
    /// <response code="404">Invoice not found</response>
    [HttpGet("{id}/pdf")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportToPdf(
        long id,
        [FromQuery] long? templateId = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/invoice/{Id}/pdf - Generating PDF (templateId: {TemplateId})", id, templateId);

        try
        {
            // Generate the PDF bytes via the export service — pass optional templateId for template selection
            var pdfBytes = await _pdfExportService.GenerateInvoicePdfAsync(id, templateId, cancellationToken);

            // Fetch the invoice to get the document number and document type for the file name
            var invoice = await _invoiceService.GetInvoiceByIdAsync(id, cancellationToken);
            // Use document type prefix — "Invoice" for invoices, "CreditNote" for credit notes
            var prefix = invoice?.DocumentType == EDocumentType.CreditNote ? "CreditNote" : "Invoice";
            var fileName = $"{prefix}_{invoice?.DocumentNumber ?? id.ToString()}.pdf";

            _logger.LogInformation("PDF generated for invoice {Id}, size: {Size} bytes", id, pdfBytes.Length);

            // Return the PDF as a downloadable file
            return File(pdfBytes, "application/pdf", fileName);
        }
        catch (KeyNotFoundException)
        {
            _logger.LogWarning("Invoice {Id} not found for PDF export", id);
            return NotFound(new { message = $"Invoice with ID {id} not found" });
        }
    }

    /// <summary>
    /// Sends the specified invoice as an email with PDF attachment.
    /// Generates the PDF, attaches it to an email, and sends it via SMTP.
    /// </summary>
    /// <param name="id">Invoice ID to send</param>
    /// <param name="dto">Email recipient and optional subject/message</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Success confirmation</returns>
    /// <response code="200">Email sent successfully</response>
    /// <response code="404">Invoice not found</response>
    /// <response code="400">Invalid email request</response>
    [HttpPost("{id}/send-email")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SendInvoiceEmail(
        long id,
        [FromBody] SendInvoiceEmailDto dto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("POST /api/invoice/{Id}/send-email to {Email}", id, dto.RecipientEmail);

        try
        {
            await _emailService.SendInvoiceEmailAsync(id, dto.RecipientEmail, cancellationToken);

            _logger.LogInformation("Invoice {Id} sent via email to {Email}", id, dto.RecipientEmail);
            return Ok(new { message = $"Invoice email sent to {dto.RecipientEmail}" });
        }
        catch (KeyNotFoundException)
        {
            _logger.LogWarning("Invoice {Id} not found for email sending", id);
            return NotFound(new { message = $"Invoice with ID {id} not found" });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Failed to send invoice {Id} email: {Message}", id, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    // ─── QR Code Endpoints ───────────────────────────────────────────────────

    /// <summary>
    /// Generates a QR code image (PNG) for the given invoice.
    /// If the invoice has a valid IBAN, generates combined "QR Platba+F" (payment + invoice data).
    /// If no IBAN is available, generates "QR Faktura" only (invoice data without payment).
    /// </summary>
    /// <param name="id">Invoice ID</param>
    /// <param name="size">QR module size in pixels (default 10, range 5-20)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>PNG image of the QR code</returns>
    [HttpGet("{id}/qr")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetQrCode(
        [FromRoute] long id,
        [FromQuery] int size = 10,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/invoice/{Id}/qr - Generating QR code", id);

        // Clamp module size to reasonable range (5-20 pixels per module)
        size = Math.Clamp(size, 5, 20);

        try
        {
            var pngBytes = await _qrPaymentService.GenerateQrCodeImageAsync(id, size, cancellationToken);
            return File(pngBytes, "image/png", $"QR_Invoice_{id}.png");
        }
        catch (KeyNotFoundException)
        {
            _logger.LogWarning("Invoice {Id} not found for QR code generation", id);
            return NotFound(new { message = $"Invoice with ID {id} not found" });
        }
    }

    /// <summary>
    /// Returns the raw SIND (Short Invoice Descriptor) string for the given invoice.
    /// This is the QR Faktura format — useful for debugging or external QR generation.
    /// </summary>
    /// <param name="id">Invoice ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>SIND string</returns>
    [HttpGet("{id}/qr/sind")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSindString(
        [FromRoute] long id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/invoice/{Id}/qr/sind - Generating SIND string", id);

        try
        {
            var sind = await _qrPaymentService.GenerateSindStringAsync(id, cancellationToken);
            return Ok(new { sind });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { message = $"Invoice with ID {id} not found" });
        }
    }

    /// <summary>
    /// Returns the SPD (Short Payment Descriptor) string with integrated QR Faktura data.
    /// This is the combined "QR Platba+F" format.
    /// If the invoice has no IBAN, returns the SIND string instead.
    /// </summary>
    /// <param name="id">Invoice ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>SPD or SIND string</returns>
    [HttpGet("{id}/qr/spd")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSpdString(
        [FromRoute] long id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/invoice/{Id}/qr/spd - Generating SPD string", id);

        try
        {
            var spd = await _qrPaymentService.GenerateSpdWithInvoiceAsync(id, cancellationToken);
            return Ok(new { spd });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { message = $"Invoice with ID {id} not found" });
        }
    }

    // ─── Bulk Operation Endpoints ────────────────────────────────────────────

    /// <summary>
    /// Completes (issues) multiple draft invoices in a single batch.
    /// Each invoice is processed individually — partial success is possible.
    /// </summary>
    /// <param name="request">List of invoice IDs to complete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Bulk operation result with success/failure counts</returns>
    [HttpPost("bulk/complete")]
    [ProducesResponseType(typeof(BulkOperationResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<BulkOperationResult>> BulkComplete(
        [FromBody] BulkOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("POST /api/invoice/bulk/complete - {Count} invoices", request.InvoiceIds.Count);
        var result = await _invoiceService.BulkCompleteAsync(request.InvoiceIds, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Marks multiple completed invoices as paid in a single batch.
    /// Each invoice is processed individually — partial success is possible.
    /// </summary>
    /// <param name="request">List of invoice IDs to mark as paid</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Bulk operation result with success/failure counts</returns>
    [HttpPost("bulk/mark-paid")]
    [ProducesResponseType(typeof(BulkOperationResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<BulkOperationResult>> BulkMarkAsPaid(
        [FromBody] BulkOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("POST /api/invoice/bulk/mark-paid - {Count} invoices", request.InvoiceIds.Count);
        var result = await _invoiceService.BulkMarkAsPaidAsync(request.InvoiceIds, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Soft-deletes multiple draft invoices in a single batch.
    /// Each invoice is processed individually — partial success is possible.
    /// </summary>
    /// <param name="request">List of invoice IDs to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Bulk operation result with success/failure counts</returns>
    [HttpPost("bulk/delete")]
    [ProducesResponseType(typeof(BulkOperationResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<BulkOperationResult>> BulkDelete(
        [FromBody] BulkOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("POST /api/invoice/bulk/delete - {Count} invoices", request.InvoiceIds.Count);
        var result = await _invoiceService.BulkDeleteAsync(request.InvoiceIds, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Sends invoice email for multiple invoices in a single batch.
    /// Each email is sent individually — partial success is possible.
    /// Uses the default recipient email from each invoice's client contact.
    /// </summary>
    /// <param name="request">List of invoice IDs to send emails for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Bulk operation result with success/failure counts</returns>
    [HttpPost("bulk/send-email")]
    [ProducesResponseType(typeof(BulkOperationResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<BulkOperationResult>> BulkSendEmail(
        [FromBody] BulkOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("POST /api/invoice/bulk/send-email - {Count} invoices", request.InvoiceIds.Count);
        var result = new BulkOperationResult();

        // Process each invoice sequentially — send email with client's default email address
        foreach (var id in request.InvoiceIds)
        {
            try
            {
                // Resolve the default email from the invoice's client contact
                var invoice = await _invoiceService.GetInvoiceByIdAsync(id, cancellationToken);
                if (invoice == null)
                {
                    result.FailedCount++;
                    result.Errors.Add(new BulkOperationError { InvoiceId = id, Error = "Invoice not found" });
                    continue;
                }

                // Use the client name as a fallback — actual email resolution happens in EmailService
                await _emailService.SendInvoiceEmailAsync(id, invoice.ClientName, cancellationToken);
                result.SuccessCount++;
            }
            catch (Exception ex)
            {
                result.FailedCount++;
                result.Errors.Add(new BulkOperationError { InvoiceId = id, Error = ex.Message });
                _logger.LogWarning("Bulk send-email failed for invoice {Id}: {Error}", id, ex.Message);
            }
        }

        return Ok(result);
    }

    /// <summary>
    /// Generates PDFs for multiple invoices and returns them as a ZIP archive.
    /// Useful for bulk downloading invoices for printing or archiving.
    /// </summary>
    /// <param name="ids">Comma-separated list of invoice IDs</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>ZIP file containing individual PDF files</returns>
    [HttpGet("bulk/pdf")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> BulkExportPdf(
        [FromQuery] string ids,
        CancellationToken cancellationToken = default)
    {
        // Parse comma-separated IDs and deduplicate to prevent duplicate entries in the ZIP
        var invoiceIds = ids.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => long.TryParse(s.Trim(), out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        _logger.LogInformation("GET /api/invoice/bulk/pdf - {Count} invoices", invoiceIds.Count);

        // Generate ZIP with all PDFs using System.IO.Compression (built-in .NET)
        using var zipStream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            foreach (var id in invoiceIds)
            {
                try
                {
                    var pdfBytes = await _pdfExportService.GenerateInvoicePdfAsync(id, null, cancellationToken);
                    var invoice = await _invoiceService.GetInvoiceByIdAsync(id, cancellationToken);
                    // Use document type prefix — "Invoice" for invoices, "CreditNote" for credit notes
                    var prefix = invoice?.DocumentType == EDocumentType.CreditNote ? "CreditNote" : "Invoice";
                    var fileName = $"{prefix}_{invoice?.DocumentNumber ?? id.ToString()}.pdf";

                    // Add each PDF as an entry in the ZIP archive
                    var entry = archive.CreateEntry(fileName, System.IO.Compression.CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    await entryStream.WriteAsync(pdfBytes, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Bulk PDF failed for invoice {Id}: {Error}", id, ex.Message);
                    // Skip failed invoices — include only successful ones in the ZIP
                }
            }
        }

        // Return the ZIP as a downloadable file
        zipStream.Position = 0;
        // ZIP archive name — "Documents" is a neutral term covering both invoices and credit notes
        return File(zipStream.ToArray(), "application/zip", $"Documents_{DateTime.UtcNow:yyyyMMdd}.zip");
    }
}
