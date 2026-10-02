using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.PaymentMatching;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>Bank statement file import (GPC / ABO format) for the Payments page.</summary>
[ApiController]
[Authorize]
[Route("api/bank-statement")]
[Produces("application/json")]
public class BankStatementController : ControllerBase
{
    private const long MaxFileSizeBytes = 5 * 1024 * 1024;
    private static readonly string[] AllowedExtensions = [".gpc", ".abo", ".txt"];

    private readonly IBankStatementImportService _importService;
    private readonly ILogger<BankStatementController> _logger;

    public BankStatementController(IBankStatementImportService importService, ILogger<BankStatementController> logger)
    {
        _importService = importService;
        _logger = logger;
    }

    /// <summary>
    /// Imports a GPC/ABO statement: creates new bank transactions (duplicates are skipped) and
    /// auto-matches incoming payments to invoices. Optional <c>bankAccountId</c> forces the target account.
    /// </summary>
    [HttpPost("import")]
    // Transport ceiling above the 5 MB file rule so our own 400 message is returned instead of a bare 413.
    [RequestSizeLimit(MaxFileSizeBytes * 2)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(BankStatementImportResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BankStatementImportResultDto>> Import(
        IFormFile? file, [FromForm] long? bankAccountId, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { message = "No file uploaded." });
        if (file.Length > MaxFileSizeBytes)
            return BadRequest(new { message = $"The file exceeds the maximum size of {MaxFileSizeBytes / (1024 * 1024)} MB." });
        if (!AllowedExtensions.Contains(Path.GetExtension(file.FileName), StringComparer.OrdinalIgnoreCase))
            return BadRequest(new { message = "Unsupported file type. Use .gpc, .abo or .txt." });

        _logger.LogInformation("GPC import requested: {Size} bytes", file.Length);

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var result = await _importService.ImportAsync(ms.ToArray(), bankAccountId, ct);
        if (result.Statements == 0)
            return BadRequest(new { message = "No GPC/ABO statement (074 record) found in the file.", errors = result.Errors });
        return Ok(result);
    }
}
