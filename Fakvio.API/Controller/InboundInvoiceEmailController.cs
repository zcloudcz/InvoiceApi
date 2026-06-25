using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.InvoiceEmail;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Fakvio.API.Controller;

/// <summary>
/// Inbox view for inbound invoice emails — list, detail, retry, ignore.
/// </summary>
[ApiController]
[Route("api/inbound-invoice-email")]
[Produces("application/json")]
[Authorize]
public class InboundInvoiceEmailController : ControllerBase
{
    private readonly TenantDbContext _context;
    private readonly ILogger<InboundInvoiceEmailController> _logger;

    public InboundInvoiceEmailController(
        TenantDbContext context,
        ILogger<InboundInvoiceEmailController> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>Paginated inbox list with filters.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<InboundInvoiceEmailDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<InboundInvoiceEmailDto>>> GetList(
        [FromQuery] InboundInvoiceEmailFilterDto filter,
        CancellationToken ct = default)
    {
        var query = _context.InboundInvoiceEmail.AsNoTracking().AsQueryable();

        if (filter.Status.HasValue)
            query = query.Where(e => e.Status == filter.Status.Value);

        if (filter.Direction.HasValue)
            query = query.Where(e => e.Direction == filter.Direction.Value);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var pattern = $"%{filter.Search}%";
            query = query.Where(e =>
                EF.Functions.ILike(e.FromAddress, pattern) ||
                (e.Subject != null && EF.Functions.ILike(e.Subject, pattern)));
        }

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(e => e.ServerReceivedAt)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(e => new InboundInvoiceEmailDto
            {
                Id = e.Id,
                FromAddress = e.FromAddress,
                FromDisplayName = e.FromDisplayName,
                Subject = e.Subject,
                EmailDate = e.EmailDate,
                ServerReceivedAt = e.ServerReceivedAt,
                Status = e.Status,
                Direction = e.Direction,
                ClassificationConfidence = e.ClassificationConfidence,
                StatusError = e.StatusError,
                ReceivedInvoiceId = e.ReceivedInvoiceId,
                InvoiceId = e.InvoiceId,
                AttachmentCount = e.AttachmentCount,
                HasPdf = e.HasPdf,
                HasIsdoc = e.HasIsdoc,
            })
            .ToListAsync(ct);

        return Ok(new PagedResult<InboundInvoiceEmailDto>(items, totalCount, filter.Page, filter.PageSize));
    }

    /// <summary>Get single email detail.</summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType(typeof(InboundInvoiceEmailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InboundInvoiceEmailDto>> GetDetail(long id, CancellationToken ct = default)
    {
        var e = await _context.InboundInvoiceEmail
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, ct);

        if (e == null) return NotFound();

        return Ok(new InboundInvoiceEmailDto
        {
            Id = e.Id,
            FromAddress = e.FromAddress,
            FromDisplayName = e.FromDisplayName,
            Subject = e.Subject,
            EmailDate = e.EmailDate,
            ServerReceivedAt = e.ServerReceivedAt,
            Status = e.Status,
            Direction = e.Direction,
            ClassificationConfidence = e.ClassificationConfidence,
            StatusError = e.StatusError,
            ReceivedInvoiceId = e.ReceivedInvoiceId,
            InvoiceId = e.InvoiceId,
            AttachmentCount = e.AttachmentCount,
            HasPdf = e.HasPdf,
            HasIsdoc = e.HasIsdoc,
        });
    }

    /// <summary>Mark email as ignored (user decided it's not an invoice).</summary>
    [HttpPost("{id:long}/ignore")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Ignore(long id, CancellationToken ct = default)
    {
        var email = await _context.InboundInvoiceEmail.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (email == null) return NotFound();

        email.Status = EInvoiceEmailStatus.Ignored;
        await _context.SaveChangesAsync(ct);
        return NoContent();
    }
}
