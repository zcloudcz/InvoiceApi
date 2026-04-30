using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.AppLog;
using Fakvio.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for viewing application logs. SysAdmin only for read endpoints;
/// the client log forwarding endpoint is open to any authenticated user so UI errors
/// can reach the server-side DatabaseLogger → AppLog table.
/// </summary>
[ApiController]
[Route("api/logs")]
[Produces("application/json")]
[Authorize(Roles = "SysAdmin")]
public class AppLogController : ControllerBase
{
    private readonly MasterDbContext _context;
    private readonly ILogger<AppLogController> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public AppLogController(
        MasterDbContext context,
        ILogger<AppLogController> logger,
        ILoggerFactory loggerFactory)
    {
        _context = context;
        _logger = logger;
        _loggerFactory = loggerFactory;
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

    /// <summary>
    /// Forwards a client-side log entry from the WASM UI into the server log pipeline.
    /// Open to any authenticated user (SysAdmin restriction is overridden) — without this,
    /// UI errors would only land in the browser console and never make it to the AppLog table.
    ///
    /// The entry is re-emitted through ILoggerFactory using the supplied Source as the category,
    /// so it flows through DatabaseLogger like any other server log (CorrelationId picked up from
    /// the X-Correlation-Id header set by CorrelationIdHandler).
    ///
    /// Defensive: never returns 4xx/5xx for malformed entries — silently coerces to a safe log
    /// so that error-on-error feedback loops are impossible.
    /// </summary>
    [HttpPost("client")]
    [AllowAnonymous] // UI may need to log a 401 *before* the user is fully authenticated; auth still attempted via JWT if present
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult LogFromClient([FromBody] ClientLogDto dto)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Message))
            return NoContent(); // Don't reward bad payloads with errors — just drop them.

        // Use the supplied source as the logger category so AppLog.Source reflects which UI piece reported it.
        // Fallback prefix "Fakvio.UI" makes filtering easy in the Logs viewer.
        var category = string.IsNullOrWhiteSpace(dto.Source)
            ? "Fakvio.UI"
            : $"Fakvio.UI.{dto.Source}";

        var logger = _loggerFactory.CreateLogger(category);

        // Parse severity defensively — anything we don't recognize becomes Error so it isn't silently swallowed.
        if (!Enum.TryParse<LogLevel>(dto.Level, ignoreCase: true, out var level))
            level = LogLevel.Error;

        // Synthesize the message — include URL when present so the SysAdmin can see where it happened.
        var message = string.IsNullOrWhiteSpace(dto.Url)
            ? dto.Message
            : $"{dto.Message} (url: {dto.Url})";

        // Reconstruct a thin Exception-like wrapper when the client supplied a stack/details string.
        // We never get the original exception object across the wire, so a plain wrapper is enough
        // to make AppLog.Exception non-null and visible in the Logs UI.
        Exception? wrappedException = string.IsNullOrWhiteSpace(dto.Exception)
            ? null
            : new ClientReportedException(dto.Exception);

        logger.Log(level, wrappedException, "{ClientMessage}", message);

        return NoContent();
    }

    /// <summary>
    /// Marker exception type for client-reported errors. Carries the client's stack trace string
    /// in Message so DatabaseLogger.Exception captures it without losing context.
    /// </summary>
    private sealed class ClientReportedException : Exception
    {
        public ClientReportedException(string clientStack) : base(clientStack) { }
    }
}
