using System.Security.Claims;
using Fakvio.Application.Common.Helpers;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.PaymentMatching;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Application.Service;
using Fakvio.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for managing received (incoming) invoices — expenses from suppliers.
/// Provides CRUD operations and status lifecycle management (Received -> Approved -> Paid).
/// All endpoints require authentication; tenant isolation via CompanyId in JWT.
/// </summary>
[ApiController]
[Route("api/received-invoice")]
[Produces("application/json")]
[Authorize]
public class ReceivedInvoiceController : ControllerBase
{
    private readonly IReceivedInvoiceService _service;
    private readonly IPaymentMatchingService _paymentMatcher;
    private readonly IFileAttachmentService _fileAttachmentService;
    private readonly IIsdocExportService _isdocExportService;
    private readonly ILogger<ReceivedInvoiceController> _logger;

    public ReceivedInvoiceController(
        IReceivedInvoiceService service,
        IPaymentMatchingService paymentMatcher,
        IFileAttachmentService fileAttachmentService,
        IIsdocExportService isdocExportService,
        ILogger<ReceivedInvoiceController> logger)
    {
        _service = service;
        _paymentMatcher = paymentMatcher;
        _fileAttachmentService = fileAttachmentService;
        _isdocExportService = isdocExportService;
        _logger = logger;
    }

    /// <summary>Reads the UserId JWT claim. Returns null when missing / malformed.</summary>
    private long? GetUserId()
    {
        var raw = User.FindFirstValue("UserId");
        return long.TryParse(raw, out var id) ? id : null;
    }

    /// <summary>
    /// Gets all received invoices with optional filtering.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<ReceivedInvoiceDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ReceivedInvoiceDto>>> GetAll(
        [FromQuery] EReceivedInvoiceStatus? status = null,
        [FromQuery] long? supplierId = null,
        CancellationToken ct = default)
    {
        var result = await _service.GetAllAsync(status, supplierId, ct);
        return Ok(result);
    }

    /// <summary>
    /// Gets paginated, filtered and sorted received invoices.
    /// Used by UI data grids with server-side pagination.
    /// </summary>
    [HttpGet("paged")]
    [ProducesResponseType(typeof(PagedResult<ReceivedInvoiceDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<ReceivedInvoiceDto>>> GetPaged(
        [FromQuery] ReceivedInvoiceFilterDto filter,
        CancellationToken ct = default)
    {
        var result = await _service.GetPagedAsync(filter, ct);
        return Ok(result);
    }

    /// <summary>
    /// Gets a specific received invoice by ID.
    /// </summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType(typeof(ReceivedInvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReceivedInvoiceDto>> GetById(long id, CancellationToken ct = default)
    {
        var result = await _service.GetByIdAsync(id, ct);
        if (result is null) return NotFound();
        return Ok(result);
    }

    /// <summary>
    /// Creates a new received invoice.
    /// Automatically calculates item totals and invoice totals.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ReceivedInvoiceDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ReceivedInvoiceDto>> Create(
        [FromBody] CreateReceivedInvoiceDto dto,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _service.CreateAsync(dto, ct);
            return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Updates an existing received invoice.
    /// Only allowed in Received or Approved status.
    /// </summary>
    [HttpPut("{id:long}")]
    [ProducesResponseType(typeof(ReceivedInvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReceivedInvoiceDto>> Update(
        long id,
        [FromBody] UpdateReceivedInvoiceDto dto,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _service.UpdateAsync(id, dto, ct);
            if (result is null) return NotFound();
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Approves a received invoice for payment.
    /// Transition: Received -> Approved.
    /// </summary>
    [HttpPost("{id:long}/approve")]
    [ProducesResponseType(typeof(ReceivedInvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReceivedInvoiceDto>> Approve(long id, CancellationToken ct = default)
    {
        try
        {
            var result = await _service.ApproveAsync(id, ct);
            if (result is null) return NotFound();
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Marks a received invoice as paid.
    /// Transition: Approved -> Paid.
    /// </summary>
    [HttpPost("{id:long}/mark-paid")]
    [ProducesResponseType(typeof(ReceivedInvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReceivedInvoiceDto>> MarkAsPaid(
        long id,
        [FromQuery] DateTime? paidAt = null,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _service.MarkAsPaidAsync(id, paidAt, ct);
            if (result is null) return NotFound();
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Rejects a received invoice.
    /// Transition: Received -> Rejected.
    /// </summary>
    [HttpPost("{id:long}/reject")]
    [ProducesResponseType(typeof(ReceivedInvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReceivedInvoiceDto>> Reject(long id, CancellationToken ct = default)
    {
        try
        {
            var result = await _service.RejectAsync(id, ct);
            if (result is null) return NotFound();
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Soft-deletes a received invoice.
    /// Only allowed in Received or Rejected status.
    /// </summary>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct = default)
    {
        try
        {
            var deleted = await _service.DeleteAsync(id, ct);
            if (!deleted) return NotFound();
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    // ─── ISDOC export ─────────────────────────────────────────────────────

    /// <summary>
    /// Exports the specified received invoice as an ISDOC 6.0.2 XML file.
    /// The supplier on the document is the invoice's supplier; the customer is
    /// the tenant's own company. Importable into Pohoda, Money S3, Helios etc.
    /// </summary>
    /// <param name="id">Received invoice ID to export</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">Returns .isdoc XML file</response>
    /// <response code="404">Received invoice not found</response>
    [HttpGet("{id:long}/isdoc")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportIsdoc(long id, CancellationToken ct = default)
    {
        _logger.LogInformation("GET /api/received-invoice/{Id}/isdoc - Generating ISDOC export", id);

        try
        {
            var isdocBytes = await _isdocExportService.ExportReceivedInvoiceAsync(id, ct);

            // Fetch the invoice to build a meaningful file name from the supplier's document number
            var invoice = await _service.GetByIdAsync(id, ct);
            var fileName = $"ReceivedInvoice_{invoice?.DocumentNumber ?? id.ToString()}.isdoc";

            _logger.LogInformation("ISDOC generated for received invoice {Id}, size: {Size} bytes", id, isdocBytes.Length);

            return File(isdocBytes, "application/xml", fileName);
        }
        catch (KeyNotFoundException)
        {
            _logger.LogWarning("Received invoice {Id} not found for ISDOC export", id);
            return NotFound(new { message = $"Received invoice with ID {id} not found" });
        }
    }

    /// <summary>
    /// Generates ISDOC 6.0.2 XML exports for multiple received invoices and
    /// returns them as a ZIP archive. Invoices that fail to export are silently
    /// skipped (mirrors the bulk attachment download behavior).
    /// </summary>
    /// <param name="ids">Comma-separated list of received invoice IDs.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>ZIP file containing individual .isdoc files</returns>
    [HttpGet("bulk/isdoc")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> BulkExportIsdoc(
        [FromQuery] string ids,
        CancellationToken ct = default)
    {
        // Parse comma-separated IDs and deduplicate (same pattern as bulk attachments)
        var invoiceIds = (ids ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => long.TryParse(s.Trim(), out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        if (invoiceIds.Count == 0)
            return BadRequest(new { message = "No valid invoice ids provided." });

        _logger.LogInformation("GET /api/received-invoice/bulk/isdoc - {Count} invoices", invoiceIds.Count);

        // Tenant-scoped lookup for file names: ids from another tenant are absent → skipped.
        var documentNumbers = await _service.GetDocumentNumbersAsync(invoiceIds, ct);

        var entries = new List<(string EntryName, byte[] Content)>();
        // Supplier document numbers can collide across suppliers — UniqueEntryName
        // deduplicates with a numeric suffix.
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var invoiceId in invoiceIds)
        {
            if (!documentNumbers.TryGetValue(invoiceId, out var documentNumber))
                continue; // unknown / foreign / deleted id

            try
            {
                var isdocBytes = await _isdocExportService.ExportReceivedInvoiceAsync(invoiceId, ct);
                var fileName = ZipArchiveHelper.SanitizePathSegment(
                    $"ReceivedInvoice_{documentNumber ?? invoiceId.ToString()}") + ".isdoc";

                entries.Add((ZipArchiveHelper.UniqueEntryName(usedNames, fileName), isdocBytes));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Bulk ISDOC failed for received invoice {Id}: {Error}", invoiceId, ex.Message);
                // Skip failed invoices — include only successful ones in the ZIP
            }
        }

        if (entries.Count == 0)
            return NotFound(new { message = "No ISDOC exports could be generated for the selected invoices." });

        var zipBytes = ZipArchiveHelper.CreateZip(entries);
        return File(zipBytes, "application/zip", $"Isdoc_{DateTime.UtcNow:yyyyMMdd}.zip");
    }

    // ─── Payment matching ─────────────────────────────────────────────────

    /// <summary>
    /// Returns all PaymentMatch rows linked to the given received invoice.
    /// Used by the Payments panel on the ReceivedInvoiceDetail page.
    /// </summary>
    /// <response code="200">List of matched payments (may be empty)</response>
    /// <response code="404">Received invoice not found</response>
    [HttpGet("{id:long}/payments")]
    [ProducesResponseType(typeof(IReadOnlyList<PaymentMatchDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<PaymentMatchDto>>> GetPayments(
        long id,
        CancellationToken ct = default)
    {
        _logger.LogInformation("GET /api/received-invoice/{Id}/payments", id);

        var invoice = await _service.GetByIdAsync(id, ct);
        if (invoice is null)
            return NotFound(new { message = $"ReceivedInvoice {id} not found." });

        var payments = await _paymentMatcher.GetPaymentsForReceivedInvoiceAsync(id, ct);
        return Ok(payments);
    }

    /// <summary>
    /// Searches unmatched bank transactions for a candidate that automatically matches
    /// the given received invoice. Returns the best proposal or 204 NoContent when nothing found.
    /// Used by the "Automaticky spárovat" button on the received invoice detail page.
    /// </summary>
    /// <response code="200">Match proposal found</response>
    /// <response code="204">No matching transaction found</response>
    /// <response code="404">Received invoice not found</response>
    [HttpPost("{id:long}/auto-match")]
    [ProducesResponseType(typeof(AutoMatchProposalDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> FindAutoMatch(long id, CancellationToken ct = default)
    {
        _logger.LogInformation("POST /api/received-invoice/{Id}/auto-match", id);

        var invoice = await _service.GetByIdAsync(id, ct);
        if (invoice is null)
            return NotFound(new { message = $"ReceivedInvoice {id} not found." });

        var proposal = await _paymentMatcher.FindAutoMatchForReceivedInvoiceAsync(id, ct);
        if (proposal == null)
            return NoContent();

        return Ok(proposal);
    }

    // ─── Bulk attachment download ─────────────────────────────────────────

    /// <summary>
    /// Maximum invoice ids accepted by the bulk attachment endpoint.
    /// Matches the grid's max page size — a sane upper bound for one ZIP.
    /// </summary>
    private const int MaxBulkAttachmentIds = 100;

    /// <summary>
    /// Downloads attachments of multiple received invoices as one ZIP archive.
    /// ZIP layout: one folder per invoice named by its document number (or id when
    /// the document number is missing), e.g. "FAK-2026-001/scan.pdf". Folders
    /// sidestep cross-invoice file name collisions; collisions within one invoice
    /// are deduplicated with a numeric suffix.
    ///
    /// Ids unknown in this tenant and invoices without attachments are silently
    /// skipped (mirrors the bulk PDF export behavior).
    /// </summary>
    /// <param name="ids">Comma-separated list of received invoice IDs.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>ZIP file, 404 when nothing to download, 400 when too many ids.</returns>
    [HttpGet("bulk/attachments")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> BulkDownloadAttachments(
        [FromQuery] string ids,
        CancellationToken ct = default)
    {
        // Parse comma-separated IDs and deduplicate (same pattern as bulk PDF export)
        var invoiceIds = (ids ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => long.TryParse(s.Trim(), out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        if (invoiceIds.Count == 0)
            return BadRequest(new { message = "No valid invoice ids provided." });

        if (invoiceIds.Count > MaxBulkAttachmentIds)
            return BadRequest(new { message = $"Too many ids — maximum is {MaxBulkAttachmentIds}." });

        _logger.LogInformation("GET /api/received-invoice/bulk/attachments - {Count} invoices", invoiceIds.Count);

        // Tenant-scoped lookup: ids from another tenant are simply absent here → skipped.
        var documentNumbers = await _service.GetDocumentNumbersAsync(invoiceIds, ct);

        var entries = new List<(string EntryName, byte[] Content)>();
        foreach (var invoiceId in invoiceIds)
        {
            if (!documentNumbers.TryGetValue(invoiceId, out var documentNumber))
                continue; // unknown / foreign / deleted id

            var attachments = await _fileAttachmentService.DownloadByEntityAsync(
                "ReceivedInvoice", invoiceId, ct);
            if (attachments.Count == 0)
                continue; // invoice without attachments

            var folder = ZipArchiveHelper.SanitizePathSegment(documentNumber ?? invoiceId.ToString());
            // Names must be unique per folder only — each invoice gets its own set.
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (content, meta) in attachments)
            {
                var entryName = ZipArchiveHelper.UniqueEntryName(usedNames, meta.OriginalFileName);
                entries.Add(($"{folder}/{entryName}", content));
            }
        }

        if (entries.Count == 0)
            return NotFound(new { message = "Selected invoices have no attachments." });

        var zipBytes = ZipArchiveHelper.CreateZip(entries);
        return File(zipBytes, "application/zip", $"Attachments_{DateTime.UtcNow:yyyyMMdd}.zip");
    }
}
