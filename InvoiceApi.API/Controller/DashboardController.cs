using InvoiceApi.Contracts.Dto.AppLog;
using InvoiceApi.Contracts.Dto.Dashboard;
using InvoiceApi.Application.Service;
using InvoiceApi.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InvoiceApi.API.Controller;

/// <summary>
/// Controller for dashboard endpoints.
/// - GET /api/dashboard — tenant dashboard (invoice stats for current company)
/// - GET /api/dashboard/sysadmin — SysAdmin dashboard (system-wide company + log stats)
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize] // All endpoints require authentication
public class DashboardController : ControllerBase
{
    private readonly IDashboardService _dashboardService;
    private readonly MasterDbContext _masterContext;
    private readonly ILogger<DashboardController> _logger;

    public DashboardController(
        IDashboardService dashboardService,
        MasterDbContext masterContext,
        ILogger<DashboardController> logger)
    {
        _dashboardService = dashboardService;
        _masterContext = masterContext;
        _logger = logger;
    }

    /// <summary>
    /// Gets all dashboard statistics in a single API call.
    /// Returns invoice counts, client counts, unpaid totals,
    /// recent invoices (last 5), and overdue invoices (up to 10).
    /// Data is automatically filtered by the user's company (or impersonated company for SysAdmin).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dashboard statistics DTO</returns>
    /// <response code="200">Returns dashboard statistics</response>
    [HttpGet]
    [ProducesResponseType(typeof(DashboardDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<DashboardDto>> GetDashboard(CancellationToken cancellationToken = default)
    {
        // Get the effective company ID (from JWT or impersonation middleware)
        var companyId = GetCurrentUserCompanyId();
        _logger.LogInformation("GET /api/dashboard - CompanyId: {CompanyId}", companyId);

        var dashboard = await _dashboardService.GetDashboardAsync(companyId, cancellationToken);
        return Ok(dashboard);
    }

    /// <summary>
    /// Gets SysAdmin-specific dashboard data: company statistics and recent logs.
    /// Does NOT require a tenant context — reads from the master database only.
    /// </summary>
    [HttpGet("sysadmin")]
    [Authorize(Roles = "SysAdmin")]
    [ProducesResponseType(typeof(SysAdminDashboardDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SysAdminDashboardDto>> GetSysAdminDashboard(CancellationToken ct = default)
    {
        _logger.LogInformation("GET /api/dashboard/sysadmin");

        // Count companies (issuers) from master DB
        var totalCompanies = await _masterContext.Client
            .AsNoTracking()
            .Where(c => c.IsIssuer && c.IsActive)
            .CountAsync(ct);

        // Count provisioning statuses from CompanySystemSettings
        var allSettings = await _masterContext.CompanySystemSettings
            .AsNoTracking()
            .ToListAsync(ct);

        var provisionedCount = allSettings.Count(s => s.IsProvisioned);
        var activeCount = allSettings.Count(s => s.IsProvisioned && s.IsActive);

        // Pending = companies that either have no settings or are not yet provisioned
        var companiesWithSettings = allSettings.Select(s => s.CompanyId).ToHashSet();
        var allCompanyIds = await _masterContext.Client
            .AsNoTracking()
            .Where(c => c.IsIssuer && c.IsActive)
            .Select(c => c.Id)
            .ToListAsync(ct);
        var pendingCount = allCompanyIds.Count(id => !companiesWithSettings.Contains(id))
                         + allSettings.Count(s => !s.IsProvisioned);

        // Recent Warning/Error/Critical logs — last 10 entries for quick health overview
        var recentLogs = await _masterContext.AppLog
            .AsNoTracking()
            .Where(l => l.Level == "Warning" || l.Level == "Error" || l.Level == "Critical")
            .OrderByDescending(l => l.Timestamp)
            .Take(10)
            .Select(l => new AppLogDto
            {
                Id = l.Id,
                Timestamp = l.Timestamp,
                Level = l.Level,
                Source = l.Source,
                Message = l.Message,
                Exception = l.Exception,
                UserId = l.UserId,
                CompanyId = l.CompanyId,
                RequestPath = l.RequestPath
            })
            .ToListAsync(ct);

        // Log counts by level for summary display
        var logCounts = await _masterContext.AppLog
            .AsNoTracking()
            .GroupBy(l => l.Level)
            .Select(g => new { Level = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var dto = new SysAdminDashboardDto
        {
            TotalCompanies = totalCompanies,
            ProvisionedCompanies = provisionedCount,
            ActiveCompanies = activeCount,
            PendingCompanies = pendingCount,
            RecentLogs = recentLogs,
            LogCountByLevel = logCounts.ToDictionary(c => c.Level, c => c.Count)
        };

        return Ok(dto);
    }

    /// <summary>
    /// Gets the effective company ID from JWT claims.
    /// For regular users, this is their CompanyId from JWT.
    /// For SysAdmin impersonating, the ImpersonationMiddleware sets this from X-Company-Id header.
    /// Returns null if no company context.
    /// </summary>
    private long? GetCurrentUserCompanyId()
    {
        var companyIdClaim = User.FindFirst("CompanyId")?.Value;
        return long.TryParse(companyIdClaim, out var companyId) ? companyId : null;
    }
}
