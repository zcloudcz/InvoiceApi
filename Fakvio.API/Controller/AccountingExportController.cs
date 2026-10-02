using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.AccountingExport;
using Fakvio.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// "Export do účetnictví" — generates one XML file containing issued and/or received invoices
/// for a date range, formatted for a specific accounting system (Pohoda / Money S3 / ABRA Flexi).
/// Tenant-scoped like every other controller (TenantContextMiddleware resolves the tenant DB
/// from the JWT's CompanyId claim before this controller runs).
/// </summary>
[ApiController]
[Route("api/accounting-export")]
[Produces("application/json")]
[Authorize] // All endpoints require authentication — read-only export, no extra role restriction
// (consistent with the ISDOC/UBL export endpoints on InvoiceController).
public class AccountingExportController : ControllerBase
{
    private readonly IAccountingExportService _exportService;
    private readonly ILogger<AccountingExportController> _logger;

    public AccountingExportController(IAccountingExportService exportService, ILogger<AccountingExportController> logger)
    {
        _exportService = exportService;
        _logger = logger;
    }

    /// <summary>
    /// Generates the accounting export XML file for the given system and date range.
    /// </summary>
    /// <param name="system">Target accounting system — Pohoda, MoneyS3 or AbraFlexi.</param>
    /// <param name="request">Date range and issued/received filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The generated XML file as a download.</returns>
    /// <response code="200">Returns the XML file.</response>
    /// <response code="400">Nothing selected, invalid/too long date range, too many ids, or the tenant has no issuer company (IČO) configured.</response>
    [HttpPost("{system}")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Export(
        EAccountingSystem system,
        [FromBody] AccountingExportRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (!request.IncludeIssued && !request.IncludeReceived)
            return BadRequest(new { message = "Select at least one of IncludeIssued / IncludeReceived." });

        _logger.LogInformation(
            "POST /api/accounting-export/{System} - {From:yyyy-MM-dd}..{To:yyyy-MM-dd}, issued={Issued}, received={Received}",
            system, request.From, request.To, request.IncludeIssued, request.IncludeReceived);

        try
        {
            var result = await _exportService.ExportAsync(
                system, request.From, request.To, request.IncludeIssued, request.IncludeReceived,
                request.InvoiceIds, cancellationToken);

            // Documents the target system cannot represent correctly are left out of the file; tell the
            // caller how many (and which, capped) so nobody assumes the export is complete.
            Response.Headers["X-Export-Exported"] = result.ExportedCount.ToString();
            Response.Headers["X-Export-Skipped"] = result.SkippedDocuments.Count.ToString();
            if (result.SkippedDocuments.Count > 0)
                Response.Headers["X-Export-Skipped-Documents"] = string.Join(",", result.SkippedDocuments.Take(50));
            return File(result.Content, result.ContentType, result.FileName);
        }
        catch (ArgumentException ex)
        {
            // Inverted/too long date range or too many ids — caller input problem.
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // No issuer configured, or (defensively) an unregistered system — both are
            // tenant/config problems the user can act on, not server errors.
            _logger.LogWarning("Accounting export ({System}) failed: {Message}", system, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }
}
