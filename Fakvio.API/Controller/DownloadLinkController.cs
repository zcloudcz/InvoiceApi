using System.Security.Claims;
using System.Text.RegularExpressions;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.AccountingExport;
using Fakvio.Contracts.Dto.DownloadLink;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;

namespace Fakvio.API.Controller;

/// <summary>
/// Short-lived signed download links, so AI/MCP clients can hand the user a URL instead of base64 file
/// content that burns context and cannot be turned into a file.
///
/// Flow: <c>POST /api/download-links</c> (authenticated, tenant-scoped like every other endpoint) validates a
/// whitelisted <see cref="EDownloadKind"/> + parameters and returns a URL holding an opaque Data Protection
/// token (company, user, kind, parameters; 10 minutes by default). <c>GET /api/download/{token}</c> is
/// anonymous: the token IS the credential. It rebuilds the caller's tenant context from the token and runs the
/// SAME controller action the authenticated endpoint uses, so export logic exists exactly once.
/// </summary>
[ApiController]
[Route("api")]
public class DownloadLinkController : ControllerBase
{
    /// <summary>Same cap as ReceivedInvoiceController.MaxBulkAttachmentIds (the grid's max page size).</summary>
    public const int MaxBulkIds = 100;

    // Entity names are plain identifiers ("Invoice", "ReceivedInvoice"); anything else is not a legitimate kind of record.
    private static readonly Regex EntityNamePattern = new("^[A-Za-z]{1,50}$", RegexOptions.Compiled);

    private readonly IDownloadLinkProtector _protector;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DownloadLinkController> _logger;

    public DownloadLinkController(
        IDownloadLinkProtector protector,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<DownloadLinkController> logger)
    {
        _protector = protector;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Mints a download link. Authorization is the normal pipeline: JWT / API key / OAuth token plus
    /// TenantContextMiddleware, so the link can only ever point into the caller's own tenant.
    /// </summary>
    /// <response code="200">Link created.</response>
    /// <response code="400">Missing or invalid parameters for the requested kind.</response>
    [HttpPost("download-links")]
    [Authorize]
    [ProducesResponseType(typeof(DownloadLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<DownloadLinkDto> CreateLink([FromBody] CreateDownloadLinkDto request)
    {
        var error = Validate(request);
        if (error != null)
            return BadRequest(new { message = error });

        // "CompanyId" is the effective company (own company, or the impersonated one for SysAdmin).
        if (!long.TryParse(User.FindFirstValue("CompanyId"), out var companyId))
            return Forbid();
        var userIdRaw = User.FindFirstValue("UserId") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!long.TryParse(userIdRaw, out var userId))
            return Forbid();

        var token = _protector.Protect(new DownloadTokenPayload(companyId, userId, request), out var expiresAt);

        // Log what was requested, never the token (it is a bearer credential).
        _logger.LogInformation(
            "Download link created: kind={Kind}, company={CompanyId}, user={UserId}, ids={Ids}, expires={ExpiresAt:O}",
            request.Kind, companyId, userId, FormatIds(request), expiresAt);

        // Behind the Azure edge Request.Scheme can be "http", so production should set DownloadLinks:PublicBaseUrl.
        var baseUrl = _configuration["DownloadLinks:PublicBaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = $"{Request.Scheme}://{Request.Host}";

        return Ok(new DownloadLinkDto { Url = $"{baseUrl}/api/download/{token}", ExpiresAt = expiresAt });
    }

    /// <summary>
    /// Redeems a download link. Anonymous by design; every failure (garbage, tampered, expired, wrong
    /// purpose, tenant gone) answers an identical bare 404 so nothing about the token is revealed.
    /// </summary>
    [HttpGet("download/{token}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(string token, CancellationToken ct)
    {
        // Set first so that even error responses are never cached by a proxy or the browser.
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";

        var payload = _protector.Unprotect(token);
        // Re-validate: a token is trusted only because we signed it, but the dispatcher must never see
        // a parameter combination the mint endpoint would have rejected.
        if (payload == null || Validate(payload.Parameters) != null)
            return NotFound();

        _logger.LogInformation(
            "Download link redeemed: kind={Kind}, company={CompanyId}, user={UserId}, ids={Ids}",
            payload.Parameters.Kind, payload.CompanyId, payload.UserId, FormatIds(payload.Parameters));

        // Anonymous request -> no JWT claims. Services such as FileAttachmentService / VatReportService read
        // the company from the "CompanyId" claim via ITenantResolver, so give THIS request the claims the
        // token vouches for (exactly what a JWT of that user would carry). Only done after the token
        // validated; the tenant schema itself is bound by the shared TenantScope helper (the same
        // mechanism background workers use), not by a second resolution path.
        var identity = new ClaimsIdentity(
            [
                new Claim("CompanyId", payload.CompanyId.ToString()),
                new Claim("UserId", payload.UserId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, payload.UserId.ToString())
            ],
            authenticationType: "DownloadLink");
        HttpContext.User = new ClaimsPrincipal(identity);

        IServiceScope scope;
        try
        {
            scope = await _scopeFactory.CreateTenantScopeAsync(payload.CompanyId, ct);
        }
        catch (InvalidOperationException)
        {
            // Company deleted / deprovisioned / deactivated since the link was minted.
            return NotFound();
        }

        using (scope)
        {
            // The result holds the file bytes in memory, so it stays valid after the scope is disposed.
            return await DispatchAsync(scope.ServiceProvider, payload.Parameters, ct);
        }
    }

    /// <summary>
    /// Maps each whitelisted kind to the existing controller action. Controllers are created from the
    /// tenant scope's service provider (not invoked over HTTP), so they get the tenant-bound services.
    /// A switch over a closed enum - an unknown value cannot reach a path or URL.
    /// </summary>
    private async Task<IActionResult> DispatchAsync(IServiceProvider sp, CreateDownloadLinkDto p, CancellationToken ct)
    {
        var ids = string.Join(',', p.Ids ?? []);
        return p.Kind switch
        {
            EDownloadKind.InvoicePdf => await Create<InvoiceController>(sp).ExportToPdf(p.Id!.Value, p.TemplateId, ct),
            EDownloadKind.InvoiceIsdoc => await Create<InvoiceController>(sp).ExportIsdoc(p.Id!.Value, ct),
            EDownloadKind.InvoiceUbl => await Create<InvoiceController>(sp).ExportUbl(p.Id!.Value, ct),
            EDownloadKind.InvoiceBulkPdf => await Create<InvoiceController>(sp).BulkExportPdf(ids, ct),
            EDownloadKind.InvoiceBulkIsdoc => await Create<InvoiceController>(sp).BulkExportIsdoc(ids, ct),
            EDownloadKind.InvoiceBulkUbl => await Create<InvoiceController>(sp).BulkExportUbl(ids, ct),
            EDownloadKind.ReceivedInvoiceIsdoc => await Create<ReceivedInvoiceController>(sp).ExportIsdoc(p.Id!.Value, ct),
            EDownloadKind.ReceivedInvoiceBulkIsdoc => await Create<ReceivedInvoiceController>(sp).BulkExportIsdoc(ids, ct),
            EDownloadKind.ReceivedInvoiceBulkAttachments => await Create<ReceivedInvoiceController>(sp).BulkDownloadAttachments(ids, ct),
            EDownloadKind.AttachmentFile => await Create<FileAttachmentController>(sp).Download(p.Id!.Value, ct),
            EDownloadKind.AttachmentsZip => await Create<FileAttachmentController>(sp).DownloadAll(p.EntityName!, p.Id!.Value, ct),
            EDownloadKind.AccountingExport => await Create<AccountingExportController>(sp).Export(
                p.AccountingSystem!.Value,
                new AccountingExportRequestDto
                {
                    From = p.From!.Value,
                    To = p.To!.Value,
                    IncludeIssued = p.IncludeIssued,
                    IncludeReceived = p.IncludeReceived
                },
                ct),
            EDownloadKind.VatEpoReturn => await Create<VatReportController>(sp)
                .DownloadEpoVatReturn(p.Year!.Value, p.Period!.Value, p.PeriodType!.Value, p.Goods?.ToArray(), ct),
            EDownloadKind.VatEpoControlStatement => await Create<VatReportController>(sp)
                .DownloadEpoControlStatement(p.Year!.Value, p.Period!.Value, p.PeriodType!.Value, ct),
            EDownloadKind.VatEpoSummaryStatement => await Create<VatReportController>(sp)
                .DownloadEpoSummaryStatement(p.Year!.Value, p.Period!.Value, p.PeriodType!.Value, p.Goods?.ToArray(), ct),
            _ => NotFound()
        };
    }

    /// <summary>
    /// Builds a controller with its constructor dependencies from <paramref name="sp"/> and gives it the
    /// current HttpContext (some actions read <c>Response</c> / <c>User</c>).
    /// </summary>
    private T Create<T>(IServiceProvider sp) where T : ControllerBase
    {
        var controller = ActivatorUtilities.CreateInstance<T>(sp);
        controller.ControllerContext = new ControllerContext(
            new ActionContext(HttpContext, new RouteData(), new ControllerActionDescriptor()));
        return controller;
    }

    /// <summary>Returns an error message when the parameters do not fit the kind, otherwise null.</summary>
    public static string? Validate(CreateDownloadLinkDto p)
    {
        if (!Enum.IsDefined(p.Kind))
            return "Unknown download kind.";

        bool ValidIds() => p.Ids is { Count: > 0 } && p.Ids.Count <= MaxBulkIds && p.Ids.All(i => i > 0);

        switch (p.Kind)
        {
            case EDownloadKind.InvoicePdf:
            case EDownloadKind.InvoiceIsdoc:
            case EDownloadKind.InvoiceUbl:
            case EDownloadKind.ReceivedInvoiceIsdoc:
            case EDownloadKind.AttachmentFile:
                return p.Id is > 0 ? null : "Id is required.";
            case EDownloadKind.InvoiceBulkPdf:
            case EDownloadKind.InvoiceBulkIsdoc:
            case EDownloadKind.InvoiceBulkUbl:
            case EDownloadKind.ReceivedInvoiceBulkIsdoc:
            case EDownloadKind.ReceivedInvoiceBulkAttachments:
                return ValidIds() ? null : $"Ids is required (1-{MaxBulkIds} positive ids).";
            case EDownloadKind.AttachmentsZip:
                return p.Id is > 0 && p.EntityName != null && EntityNamePattern.IsMatch(p.EntityName)
                    ? null
                    : "EntityName (letters only) and Id (record id) are required.";
            case EDownloadKind.AccountingExport:
                return p.AccountingSystem is { } system && Enum.IsDefined(system) && p.From.HasValue && p.To.HasValue
                    ? null
                    : "AccountingSystem, From and To are required.";
            default: // the three VAT EPO kinds
                return p.Year.HasValue && p.Period.HasValue && p.PeriodType is { } type && Enum.IsDefined(type)
                    ? null
                    : "Year, Period and PeriodType are required.";
        }
    }

    private static string FormatIds(CreateDownloadLinkDto p)
        => p.Ids is { Count: > 0 } ? string.Join(',', p.Ids) : p.Id?.ToString() ?? "-";
}
