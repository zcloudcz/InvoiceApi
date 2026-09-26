using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Import;
using Fakvio.Infrastructure.Import;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for importing invoices from PDF files, and clients from a CSV export
/// (Fakturoid/iDoklad — see DEVGUIDE.md §4.13).
///
/// The import follows a 2-step workflow:
/// 1. POST /preview — Upload 1-N PDFs (or 1 CSV), get extraction results + validation
/// 2. POST /confirm — Submit reviewed/edited data to create invoices/clients
///
/// The preview endpoints accept multipart/form-data with files.
/// The confirm endpoints accept JSON with user-confirmed data.
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
    private readonly IClientCsvImportService _clientCsvImportService;
    private readonly ILogger<ImportController> _logger;

    public ImportController(
        IInvoiceImportService importService,
        IClientCsvImportService clientCsvImportService,
        ILogger<ImportController> logger)
    {
        _importService = importService;
        _clientCsvImportService = clientCsvImportService;
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

    // ─── Client CSV import (N6) ───────────────────────────────────────────

    /// <summary>
    /// Previews a client CSV import: parses the file, maps columns via header aliases, and flags
    /// rows as New/Duplicate/Invalid (duplicate = same IČO already in the DB or earlier in the file).
    /// Nothing is saved — the user reviews the preview and calls /clients/confirm with the rows to keep.
    ///
    /// Accepts multipart/form-data with a single "file" (CSV export from Fakturoid or iDoklad).
    /// </summary>
    [HttpPost("clients/preview")]
    // Generous transport ceiling (multipart boundary/header overhead on top of the file itself) —
    // the actual "file too big" rule is the explicit check below, which returns our own 400
    // { message } body. Without this margin, ASP.NET Core's own request-size middleware would
    // reject an at-the-limit file with a bare 413 before this action even runs.
    [RequestSizeLimit(CsvTable.MaxFileSizeBytes * 2)]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<ClientImportPreviewDto>> PreviewClients(IFormFile? file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { message = "No file uploaded." });
        }

        if (file.Length > CsvTable.MaxFileSizeBytes)
        {
            return BadRequest(new { message = $"The file exceeds the maximum size of {CsvTable.MaxFileSizeBytes / (1024 * 1024)} MB." });
        }

        _logger.LogInformation("Client CSV import preview requested: {FileName} ({Size} bytes)", file.FileName, file.Length);

        try
        {
            await using var stream = file.OpenReadStream();
            var preview = await _clientCsvImportService.PreviewAsync(stream, ct);
            return Ok(preview);
        }
        catch (CsvParseException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Confirms a client CSV import: creates the clients the user kept from the preview.
    /// Duplicates are re-checked at this point and skipped rather than failing the whole request.
    /// </summary>
    [HttpPost("clients/confirm")]
    public async Task<ActionResult<ClientImportResultDto>> ConfirmClients(
        [FromBody] ClientImportConfirmDto request,
        CancellationToken ct)
    {
        if (request.Clients == null || request.Clients.Count == 0)
        {
            return BadRequest(new { message = "No clients to import." });
        }

        // Confirm accepts a plain JSON list, so it isn't bounded by CsvTable's own row limit —
        // enforce the same cap here, otherwise an authenticated caller could submit an arbitrarily
        // large batch (up to the general request-body limit) directly, bypassing the CSV parser entirely.
        if (request.Clients.Count > CsvTable.MaxRowCount)
        {
            return BadRequest(new { message = $"Too many clients in one request (max {CsvTable.MaxRowCount})." });
        }

        _logger.LogInformation("Client CSV import confirm requested: {Count} client(s)", request.Clients.Count);

        var result = await _clientCsvImportService.ConfirmAsync(request, ct);
        return Ok(result);
    }
}
