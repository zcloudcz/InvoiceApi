using System.Security.Claims;
using System.Text.RegularExpressions;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.AccountingExport;
using Fakvio.Contracts.Dto.DownloadLink;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Fakvio.API.Controller;

/// <summary>
/// Short-lived signed download links, so AI/MCP clients can hand the user a URL instead of base64 file
/// content that burns context and cannot be turned into a file.
///
/// Flow: <c>POST /api/download-links</c> (authenticated, tenant-scoped like every other endpoint) validates a
/// whitelisted <see cref="EDownloadKind"/> + parameters and returns a URL holding an opaque Data Protection
/// token (company, user, kind, parameters; 10 minutes by default). <c>GET /api/download/{token}</c> is
/// anonymous: the token IS the credential. It re-checks that the user who minted the link is still active and
/// still belongs to the company, rebuilds that tenant context from the token and runs the SAME controller
/// action the authenticated endpoint uses, so export logic exists exactly once.
/// </summary>
[ApiController]
[Route("api")]
public class DownloadLinkController : ControllerBase
{
    /// <summary>Same cap as ReceivedInvoiceController.MaxBulkAttachmentIds (the grid's max page size).</summary>
    public const int MaxBulkIds = 100;

    /// <summary>
    /// Most VAT ids accepted in <c>Goods</c>. The list travels inside the token, i.e. inside the URL, so an
    /// unbounded list would produce a link longer than browsers and proxies accept.
    /// </summary>
    public const int MaxGoods = 100;

    /// <summary>Longest single <c>Goods</c> entry - a VAT id is at most 14 characters, this leaves headroom.</summary>
    public const int MaxGoodsLength = 20;

    /// <summary>Name of the per-IP rate limiter policy of the anonymous download endpoint (registered in Program.cs).</summary>
    public const string RateLimitPolicy = "download-link";

    // Entity names are plain identifiers ("Invoice", "ReceivedInvoice"); anything else is not a legitimate kind of record.
    private static readonly Regex EntityNamePattern = new("^[A-Za-z]{1,50}$", RegexOptions.Compiled);

    private readonly IDownloadLinkProtector _protector;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly MasterDbContext _masterDb;
    private readonly ILogger<DownloadLinkController> _logger;

    public DownloadLinkController(
        IDownloadLinkProtector protector,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IHostEnvironment environment,
        MasterDbContext masterDb,
        ILogger<DownloadLinkController> logger)
    {
        _protector = protector;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _environment = environment;
        _masterDb = masterDb;
        _logger = logger;
    }

    /// <summary>
    /// Mints a download link. Authorization is the normal pipeline: JWT / API key / OAuth token plus
    /// TenantContextMiddleware, so the link can only ever point into the caller's own tenant.
    /// Nothing is persisted, which is why a read-only API key may call it (see ApiKeyRequestGuard).
    /// </summary>
    /// <response code="200">Link created.</response>
    /// <response code="400">
    /// Missing or invalid parameters for the requested kind, or the export itself is refused - then the body
    /// is exactly the one the export endpoint returns (e.g. <c>TENANT_NOT_READY</c>, <c>EPO_HEADER_INCOMPLETE</c>).
    /// </response>
    /// <response code="500">The server has no usable <c>DownloadLinks:PublicBaseUrl</c>.</response>
    [HttpPost("download-links")]
    [Authorize]
    [ProducesResponseType(typeof(DownloadLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<DownloadLinkDto>> CreateLink([FromBody] CreateDownloadLinkDto request, CancellationToken ct)
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

        var baseUrl = ResolvePublicBaseUrl();
        if (baseUrl == null)
        {
            // A misconfigured server must not hand out an http:// link: the token in it is a bearer
            // credential and would travel in clear text. Fail loudly so the SysAdmin sees it in the log.
            _logger.LogError(
                "Download link refused: DownloadLinks:PublicBaseUrl is missing or is not an absolute https URL. " +
                "Set it to the public origin of this API (see ADMINGUIDE).");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "Download links are not configured on this server. Contact the administrator." });
        }

        // Some exports can be refused for a reason the caller can fix (invoice not ready for UBL, EPO
        // header incomplete, no issuer, ...). The person opening the link would only see raw JSON in the
        // browser, and the AI that asked would never learn why. So run the SAME action once now and, when
        // it does not produce a file, answer with its own error body. Running the action (instead of a
        // separate "can I export" check) keeps the rules in one place; the price is that these exports are
        // generated twice, here and when the link is opened.
        int? skippedDocuments = null;
        if (request.Kind is EDownloadKind.InvoiceUbl or EDownloadKind.AccountingExport
            or EDownloadKind.VatEpoReturn or EDownloadKind.VatEpoControlStatement or EDownloadKind.VatEpoSummaryStatement)
        {
            // The reused action logs as if it served a file ("EPO DPHDP3 downloaded", "UBL generated").
            // Nothing is served here, so say so: one line for the AppLog table (DatabaseLogger ignores
            // scopes) and a scope value that marks every line of the dry run in Application Insights
            // (customDimensions.DownloadLinkDryRun).
            _logger.LogInformation(
                "Download link dry run: kind={Kind}. The export messages that follow in this request validate the export, no file is served.",
                request.Kind);

            IActionResult dryRun;
            using (_logger.BeginScope(new Dictionary<string, object> { ["DownloadLinkDryRun"] = true }))
            {
                // The request is already tenant-scoped, so the request services are the right ones here.
                dryRun = await DispatchAsync(HttpContext.RequestServices, request, ct);
            }

            // The accounting export reports how many documents the target system cannot represent only
            // in this header (AccountingExportController.Export). Pass it on in the body, so the caller
            // knows the file behind the link is incomplete.
            if (int.TryParse(Response.Headers["X-Export-Skipped"], out var skipped))
                skippedDocuments = skipped;

            // Those headers describe the file, and this response is not the file: take them off again.
            Response.Headers.Remove("X-Export-Exported");
            Response.Headers.Remove("X-Export-Skipped");

            if (dryRun is not FileResult)
                return (ActionResult)dryRun;
        }

        var token = _protector.Protect(new DownloadTokenPayload(companyId, userId, request), out var expiresAt);

        // Log what was requested, never the token (it is a bearer credential).
        _logger.LogInformation(
            "Download link created: kind={Kind}, company={CompanyId}, user={UserId}, ids={Ids}, expires={ExpiresAt:O}",
            request.Kind, companyId, userId, FormatIds(request), expiresAt);

        return Ok(new DownloadLinkDto
        {
            Url = $"{baseUrl}/api/download/{token}",
            ExpiresAt = expiresAt,
            SkippedDocuments = skippedDocuments
        });
    }

    /// <summary>
    /// Origin the link is built on, or null when this server must not issue links at all.
    /// Outside Development only an absolute https <c>DownloadLinks:PublicBaseUrl</c> is accepted - behind the
    /// Azure edge <c>Request.Scheme</c> is "http", so the request itself cannot be trusted to say "https".
    /// Development may fall back to the request (local http://localhost).
    /// </summary>
    private string? ResolvePublicBaseUrl()
    {
        if (HasHttpsPublicBaseUrl(_configuration, out var configured))
            return configured;

        if (!_environment.IsDevelopment())
            return null;

        return string.IsNullOrEmpty(configured) ? $"{Request.Scheme}://{Request.Host}" : configured;
    }

    /// <summary>
    /// True when <c>DownloadLinks:PublicBaseUrl</c> is an absolute https URL; <paramref name="configured"/> is
    /// the trimmed value either way. Static so Program.cs can warn about a bad value at startup with the
    /// very same rule, instead of the SysAdmin learning about it from the first failed export.
    /// </summary>
    public static bool HasHttpsPublicBaseUrl(IConfiguration configuration, out string? configured)
    {
        configured = configuration["DownloadLinks:PublicBaseUrl"]?.Trim().TrimEnd('/');
        return Uri.TryCreate(configured, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
    }

    /// <summary>
    /// Redeems a download link. Anonymous by design; every failure of the link itself (garbage, tampered,
    /// expired, wrong purpose, tenant gone, user deactivated or removed from the company) answers an identical
    /// bare 404 so nothing about the token is revealed. Once the link is accepted, the response is whatever
    /// the export action returns - the file, or that action's own 400/404 body.
    /// </summary>
    /// <response code="200">The file (binary, <c>Content-Disposition: attachment</c>).</response>
    /// <response code="404">The link is not valid (any more), or the requested record does not exist.</response>
    /// <response code="429">Too many downloads from this IP address.</response>
    [HttpGet("download/{token}")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
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

        // The token proves who minted the link, not that this person may still see the data. Every
        // authenticated request re-checks that in CompanyMembershipMiddleware; an anonymous request never
        // gets there, so the same rule is applied here: the user must still be active and either be a
        // SysAdmin or still hold an active membership in the token's company. One master-DB query.
        var stillAllowed = await _masterDb.User.AsNoTracking().AnyAsync(u =>
            u.Id == payload.UserId && u.IsActive &&
            (u.Role == EUserRole.SysAdmin || _masterDb.UserCompanyMembership.Any(m =>
                m.UserId == u.Id && m.CompanyId == payload.CompanyId && m.IsActive &&
                m.Company.IsActive && m.Company.IsIssuer &&
                (m.Role == EUserRole.User || m.Role == EUserRole.Admin))), ct);
        if (!stillAllowed)
        {
            _logger.LogWarning(
                "Download link refused: user {UserId} is no longer active in company {CompanyId}",
                payload.UserId, payload.CompanyId);
            return NotFound();
        }

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
                if (p.PeriodType is not { } type || !Enum.IsDefined(type))
                    return "Year, Period and PeriodType are required.";
                // Only the shape is checked here; which years actually have data is the export's business.
                if (p.Year is not (>= 2000 and <= 2100))
                    return "Year must be between 2000 and 2100.";
                var maxPeriod = type == EVatPeriodType.Monthly ? 12 : 4;
                if (p.Period is not { } period || period < 1 || period > maxPeriod)
                    return $"Period must be between 1 and {maxPeriod} for {type} reporting.";
                if (p.Goods is { } goods && (goods.Count > MaxGoods
                        || goods.Any(g => string.IsNullOrWhiteSpace(g) || g.Length > MaxGoodsLength)))
                    return $"Goods accepts at most {MaxGoods} VAT ids of at most {MaxGoodsLength} characters each.";
                return null;
        }
    }

    private static string FormatIds(CreateDownloadLinkDto p)
        => p.Ids is { Count: > 0 } ? string.Join(',', p.Ids) : p.Id?.ToString() ?? "-";
}
