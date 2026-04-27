using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.AppLog;
using Fakvio.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for viewing application logs. SysAdmin only.
/// Reads from the AppLog table in the master database.
/// Supports server-side pagination, level filtering, date range, and text search.
/// </summary>
[ApiController]
[Route("api/logs")]
[Produces("application/json")]
[Authorize(Roles = "SysAdmin")]
public class AppLogController : ControllerBase
{
    private readonly MasterDbContext _context;
    private readonly ILogger<AppLogController> _logger;

    public AppLogController(MasterDbContext context, ILogger<AppLogController> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Gets paginated log entries with optional filtering.
    /// </summary>
    /// <param name="page">Page number (1-based, default 1)</param>
    /// <param name="pageSize">Items per page (default 50, max 200)</param>
    /// <param name="level">Filter by log level (e.g., "Error", "Warning")</param>
    /// <param name="search">Search in Message and Source fields</param>
    /// <param name="from">Filter logs from this date (UTC)</param>
    /// <param name="to">Filter logs until this date (UTC)</param>
    /// <param name="ct">Cancellation token</param>
    [HttpGet("paged")]
    [ProducesResponseType(typeof(PagedResult<AppLogDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<AppLogDto>>> GetPaged(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? level = null,
        [FromQuery] string? search = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default)
    {
        // Clamp page size to prevent excessive queries
        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(page, 1);

        var query = _context.AppLog.AsNoTracking().AsQueryable();

        // Apply filters
        if (!string.IsNullOrWhiteSpace(level))
            query = query.Where(l => l.Level == level);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(l => l.Message.Contains(search) || l.Source.Contains(search));

        if (from.HasValue)
            query = query.Where(l => l.Timestamp >= from.Value);

        if (to.HasValue)
            query = query.Where(l => l.Timestamp <= to.Value);

        // Count total matching entries
        var totalCount = await query.CountAsync(ct);

        // Get the requested page, ordered by newest first
        var items = await query
            .OrderByDescending(l => l.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
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
                RequestPath = l.RequestPath,
                CorrelationId = l.CorrelationId
            })
            .ToListAsync(ct);

        return Ok(new PagedResult<AppLogDto>(items, totalCount, page, pageSize));
    }

    /// <summary>
    /// Gets a summary of log counts by level — used for the SysAdmin dashboard.
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(AppLogSummaryDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AppLogSummaryDto>> GetSummary(CancellationToken ct = default)
    {
        var counts = await _context.AppLog
            .AsNoTracking()
            .GroupBy(l => l.Level)
            .Select(g => new { Level = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var summary = new AppLogSummaryDto
        {
            CountByLevel = counts.ToDictionary(c => c.Level, c => c.Count),
            TotalCount = counts.Sum(c => c.Count)
        };

        return Ok(summary);
    }
}
