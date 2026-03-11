using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Import;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for importing invoices from PDF files.
///
/// The import follows a 2-step workflow:
/// 1. POST /preview — Upload 1-N PDFs, get extraction results + validation
/// 2. POST /confirm — Submit reviewed/edited data to create invoices
///
/// The preview endpoint accepts multipart/form-data with PDF files.
/// The confirm endpoint accepts JSON with user-confirmed invoice data.
///
/// Both issued and received invoices are supported via the "target" parameter.
/// </summary>
[ApiController]
[Route("api/import")]
[Produces("application/json")]
[Authorize]
public class ImportController : ControllerBase
{
    private readonly IInvoiceImportService _importService;
    private readonly ILogger<ImportController> _logger;

    public ImportController(
        IInvoiceImportService importService,
        ILogger<ImportController> logger)
    {
        _importService = importService;
        _logger = logger;
    }

    /// <summary>
    /// Previews the import of one or more PDF files.
    /// Extracts invoice data using the 3-tier pipeline (QR → AI → Regex),
    /// validates the data, and returns previews for user review.
    ///
    /// Accepts multipart/form-data with:
    /// - files: 1-N PDF files (max 10 MB each)
    /// - target: "IssuedInvoice" or "ReceivedInvoice"
    /// </summary>
    /// <param name="files">PDF files to import.</param>
    /// <param name="target">Import target type.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of preview DTOs (one per uploaded file).</returns>
    [HttpPost("preview")]
    [RequestSizeLimit(50_000_000)] // 50 MB total for batch uploads
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<List<InvoiceImportPreviewDto>>> Preview(
        [FromForm] List<IFormFile> files,
        [FromForm] EImportTarget target,
        CancellationToken ct)
    {
        if (files == null || files.Count == 0)
        {
            return BadRequest("No files uploaded.");
        }

        _logger.LogInformation("Import preview requested: {Count} file(s), target: {Target}",
            files.Count, target);

        var previews = new List<InvoiceImportPreviewDto>();

        foreach (var file in files)
        {
            // Validate file type
            if (!file.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) &&
                !file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                previews.Add(new InvoiceImportPreviewDto
                {
                    FileName = file.FileName,
                    ExtractionSource = "Error",
                    Validations =
                    {
                        new ImportValidationMessage
                        {
                            Field = "File",
                            Message = "Only PDF files are supported.",
                            Severity = EImportValidationSeverity.Error
                        }
                    }
                });
                continue;
            }

            // Validate file size (max 10 MB per file)
            if (file.Length > 10 * 1024 * 1024)
            {
                previews.Add(new InvoiceImportPreviewDto
                {
                    FileName = file.FileName,
                    ExtractionSource = "Error",
                    Validations =
                    {
                        new ImportValidationMessage
                        {
                            Field = "File",
                            Message = "File exceeds maximum size of 10 MB.",
                            Severity = EImportValidationSeverity.Error
                        }
                    }
                });
                continue;
            }

            // Read file bytes
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var pdfBytes = ms.ToArray();

            // Preview the import
            var preview = await _importService.PreviewImportAsync(pdfBytes, file.FileName, target, ct);
            previews.Add(preview);
        }

        return Ok(previews);
    }

    /// <summary>
    /// Confirms the import of previously previewed invoices.
    /// Creates invoices (and optionally new clients) in the database.
    ///
    /// The request body contains user-reviewed/edited data from the preview step.
    /// </summary>
    /// <param name="request">Confirmed import data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of import results (success/failure per file).</returns>
    [HttpPost("confirm")]
    public async Task<ActionResult<List<ImportResultDto>>> Confirm(
        [FromBody] ConfirmInvoiceImportRequest request,
        CancellationToken ct)
    {
        if (request.Items == null || request.Items.Count == 0)
        {
            return BadRequest("No items to import.");
        }

        _logger.LogInformation("Import confirm requested: {Count} item(s), target: {Target}",
            request.Items.Count, request.Target);

        var results = await _importService.ConfirmImportAsync(request, ct);
        return Ok(results);
    }
}
