using Fakvio.Application.Common.Extensions;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZMapper;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Implementation of IReceivedInvoiceService.
/// Handles CRUD, status transitions, and totals calculation for received (incoming) invoices.
/// Follows the same patterns as InvoiceService for consistency.
///
/// Storage: a received invoice is a row of the shared <c>Invoice</c> table with
/// <c>DocumentType = ReceivedInvoice</c> (supplier = <c>ClientId</c>, our own company = <c>IssuerId</c>).
/// This class is a facade: it keeps the old received-invoice contract (DTOs, <see cref="EReceivedInvoiceStatus"/>,
/// the allowed transitions and the validation messages) on top of the unified table. Every query goes through
/// <see cref="InvoiceQueryExtensions.ReceivedOnly"/>, so the id of an issued document is always "not found" here.
/// </summary>
public class ReceivedInvoiceService : IReceivedInvoiceService
{
    private readonly TenantDbContext _context;
    private readonly ILogger<ReceivedInvoiceService> _logger;
    private readonly DocumentTransitions _transitions; // the ONE state machine shared with InvoiceService
    private readonly ReceivedDocumentWriter _writer; // the ONE create/update rule set shared with InvoiceService

    public ReceivedInvoiceService(TenantDbContext context, ILogger<ReceivedInvoiceService> logger, IWebhookPublisher? webhookPublisher = null,
        IExchangeRateService? exchangeRateService = null)
    {
        _context = context;
        _logger = logger;
        _writer = new ReceivedDocumentWriter(context, webhookPublisher, logger);
        // Received policy: no number sequence and no invoice webhooks (null), the exchange rate is shared (§4.17).
        _transitions = new DocumentTransitions(context, null, exchangeRateService, null, logger);
    }

    // ─── Status vocabulary: unified EInvoiceStatus (storage) <-> EReceivedInvoiceStatus (contract) ───

    /// <summary>
    /// Maps the stored status to the legacy received-invoice status the contract still exposes.
    /// Draft -> Received, Completed / PartiallyPaid -> Approved (a partial payment is not visible in the old
    /// contract), Paid -> Paid, Deleted -> Deleted. "Rejected" no longer exists: reject soft-deletes.
    /// </summary>
    internal static EReceivedInvoiceStatus ToLegacy(EInvoiceStatus status) => status switch
    {
        EInvoiceStatus.Draft => EReceivedInvoiceStatus.Received,
        EInvoiceStatus.Completed or EInvoiceStatus.PartiallyPaid => EReceivedInvoiceStatus.Approved,
        EInvoiceStatus.Paid => EReceivedInvoiceStatus.Paid,
        EInvoiceStatus.Deleted => EReceivedInvoiceStatus.Deleted,
        // Creditnoted is an issued-document state; a received row can never reach it.
        _ => throw new InvalidOperationException($"Status {status} is not valid for a received invoice.")
    };

    /// <summary>
    /// Stored statuses that a legacy status filter stands for. NEVER copy the numbers 1:1 - the legacy
    /// Rejected(4) would collide with Creditnoted(4). Rejected matches nothing (abolished, such rows are Deleted).
    /// </summary>
    private static EInvoiceStatus[] ToStored(EReceivedInvoiceStatus status) => status switch
    {
        EReceivedInvoiceStatus.Received => [EInvoiceStatus.Draft],
        EReceivedInvoiceStatus.Approved => [EInvoiceStatus.Completed, EInvoiceStatus.PartiallyPaid],
        EReceivedInvoiceStatus.Paid => [EInvoiceStatus.Paid],
        EReceivedInvoiceStatus.Deleted => [EInvoiceStatus.Deleted],
        _ => []
    };

    /// <summary>
    /// Maps an Invoice row (DocumentType = ReceivedInvoice) to ReceivedInvoiceDto.
    /// Written by hand: the DTO keeps the old shape (SupplierId, EReceivedInvoiceStatus), which
    /// ZMapper's name-for-name copy cannot express. The items still go through ZMapper.
    /// </summary>
    private static ReceivedInvoiceDto MapToDto(Invoice entity) => new()
    {
        Id = entity.Id,
        DocumentNumber = entity.DocumentNumber,
        Status = ToLegacy(entity.Status),
        SupplierId = entity.ClientId ?? 0,
        SupplierName = entity.Client?.CompanyName ?? string.Empty,
        IssueDate = entity.IssueDate,
        ReceivedDate = entity.ReceivedDate,
        DueDate = entity.DueDate,
        TaxableSupplyDate = entity.TaxableSupplyDate,
        VariableSymbol = entity.VariableSymbol,
        TotalBeforeVat = entity.TotalBeforeVat,
        TotalVat = entity.TotalVat,
        TotalWithVat = entity.TotalWithVat,
        CurrencyId = entity.CurrencyId,
        CurrencyCode = entity.Currency?.Code ?? string.Empty,
        CurrencySymbol = entity.Currency?.Symbol ?? string.Empty,
        ExchangeRate = entity.ExchangeRate,
        ExchangeRateDate = entity.ExchangeRateDate,
        PaymentMethod = entity.PaymentMethod,
        BankAccountNumber = entity.BankAccountNumber,
        IBAN = entity.IBAN,
        SWIFT = entity.SWIFT,
        PaidAt = entity.PaidAt,
        PaidAmount = entity.PaidAmount,
        Notes = entity.Notes,
        // AttachmentFileName / AttachmentContentType stay null: the old columns were never written by any code.
        Items = entity.InvoiceItem.OrderBy(i => i.OrderIndex).Select(i => i.ToReceivedInvoiceItemDto()).ToList(),
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt
    };

    /// <summary>
    /// Base query with all necessary includes for read operations.
    /// AsNoTracking for performance — no entity modifications during reads.
    /// </summary>
    private IQueryable<Invoice> BaseQuery()
    {
        return _context.Invoice
            .ReceivedOnly()
            .AsNoTracking()
            .Include(r => r.Client)
            .Include(r => r.Currency)
            .Include(r => r.InvoiceItem.OrderBy(i => i.OrderIndex));
    }

    /// <inheritdoc />
    public async Task<List<ReceivedInvoiceDto>> GetAllAsync(
        EReceivedInvoiceStatus? status = null,
        long? supplierId = null,
        CancellationToken ct = default)
    {
        var query = BaseQuery()
            .Where(r => r.Status != EInvoiceStatus.Deleted);

        if (status.HasValue)
        {
            var stored = ToStored(status.Value);
            query = query.Where(r => stored.Contains(r.Status));
        }

        if (supplierId.HasValue)
            query = query.Where(r => r.ClientId == supplierId.Value);

        var entities = await query
            .OrderByDescending(r => r.ReceivedDate ?? r.IssueDate)
            .ThenByDescending(r => r.Id)
            .ToListAsync(ct);

        return entities.Select(MapToDto).ToList();
    }

    /// <inheritdoc />
    public async Task<PagedResult<ReceivedInvoiceDto>> GetPagedAsync(
        ReceivedInvoiceFilterDto filter,
        CancellationToken ct = default)
    {
        var query = BaseQuery()
            .Where(r => r.Status != EInvoiceStatus.Deleted);

        // Apply filters
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.ToLower();
            query = query.Where(r =>
                (r.DocumentNumber != null && r.DocumentNumber.ToLower().Contains(search)) ||
                r.Client!.CompanyName.ToLower().Contains(search) ||
                (r.VariableSymbol != null && r.VariableSymbol.ToLower().Contains(search)));
        }

        // Column filters — single-column contains, separate from the global Search above
        if (!string.IsNullOrWhiteSpace(filter.DocumentNumber))
        {
            var docNumber = filter.DocumentNumber.ToLower();
            query = query.Where(r => r.DocumentNumber != null && r.DocumentNumber.ToLower().Contains(docNumber));
        }

        if (!string.IsNullOrWhiteSpace(filter.SupplierName))
        {
            var supplierName = filter.SupplierName.ToLower();
            query = query.Where(r => r.Client!.CompanyName.ToLower().Contains(supplierName));
        }

        if (filter.Status.HasValue)
        {
            var stored = ToStored(filter.Status.Value);
            query = query.Where(r => stored.Contains(r.Status));
        }

        if (filter.SupplierId.HasValue)
            query = query.Where(r => r.ClientId == filter.SupplierId.Value);

        // Ensure UTC Kind — Npgsql rejects Unspecified Kind for 'timestamp with time zone'.
        if (filter.IssueDateFrom.HasValue)
            query = query.Where(r => r.IssueDate >= DateTime.SpecifyKind(filter.IssueDateFrom.Value, DateTimeKind.Utc));

        if (filter.IssueDateTo.HasValue)
            query = query.Where(r => r.IssueDate <= DateTime.SpecifyKind(filter.IssueDateTo.Value, DateTimeKind.Utc));

        if (filter.DueDateFrom.HasValue)
            query = query.Where(r => r.DueDate >= DateTime.SpecifyKind(filter.DueDateFrom.Value, DateTimeKind.Utc));

        if (filter.DueDateTo.HasValue)
            query = query.Where(r => r.DueDate <= DateTime.SpecifyKind(filter.DueDateTo.Value, DateTimeKind.Utc));

        if (filter.IsOverdue == true)
        {
            var now = DateTime.UtcNow;
            // "Approved" in the legacy vocabulary = Completed or PartiallyPaid.
            query = query.Where(r =>
                (r.Status == EInvoiceStatus.Completed || r.Status == EInvoiceStatus.PartiallyPaid) && r.DueDate < now);
        }

        if (!string.IsNullOrWhiteSpace(filter.Currency))
            query = query.Where(r => r.Currency.Code == filter.Currency);

        if (filter.MinAmount.HasValue)
            query = query.Where(r => r.TotalWithVat >= filter.MinAmount.Value);

        if (filter.MaxAmount.HasValue)
            query = query.Where(r => r.TotalWithVat <= filter.MaxAmount.Value);

        // Sorting — default by received date descending.
        // Status sorts by the LEGACY order (PartiallyPaid counts as Approved), not by the raw stored number
        // (PartiallyPaid = 6 would otherwise jump behind Deleted = 5).
        var sortBy = filter.SortBy?.ToLower() ?? "receiveddate";
        var isDesc = filter.SortDirection == null ? true : filter.IsDescending;

        query = sortBy switch
        {
            "documentnumber" => isDesc ? query.OrderByDescending(r => r.DocumentNumber) : query.OrderBy(r => r.DocumentNumber),
            "suppliername" => isDesc ? query.OrderByDescending(r => r.Client!.CompanyName) : query.OrderBy(r => r.Client!.CompanyName),
            "issuedate" => isDesc ? query.OrderByDescending(r => r.IssueDate) : query.OrderBy(r => r.IssueDate),
            "duedate" => isDesc ? query.OrderByDescending(r => r.DueDate) : query.OrderBy(r => r.DueDate),
            "totalwithvat" => isDesc ? query.OrderByDescending(r => r.TotalWithVat) : query.OrderBy(r => r.TotalWithVat),
            "status" => isDesc
                ? query.OrderByDescending(r => r.Status == EInvoiceStatus.PartiallyPaid ? EInvoiceStatus.Completed : r.Status)
                : query.OrderBy(r => r.Status == EInvoiceStatus.PartiallyPaid ? EInvoiceStatus.Completed : r.Status),
            _ => isDesc ? query.OrderByDescending(r => r.ReceivedDate) : query.OrderBy(r => r.ReceivedDate)
        };

        // Use the same paging extension as InvoiceService
        var pagedResult = await query.ToPagedResultAsync(filter.Page, filter.PageSize, ct);

        // Attachment counts for the grid's "download attachments" action.
        // One grouped query over just the page's ids (max PageSize=100 rows) —
        // avoids an N+1 per row while keeping the entity-based MapToDto untouched.
        // The EntityName string "ReceivedInvoice" is kept on purpose (stored on existing FileAttachment rows).
        var pageIds = pagedResult.Items.Select(r => r.Id).ToList();
        var attachmentCounts = await _context.FileAttachment
            .AsNoTracking()
            .Where(f => f.EntityName == "ReceivedInvoice" && pageIds.Contains(f.RecordId))
            .GroupBy(f => f.RecordId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return new PagedResult<ReceivedInvoiceDto>
        {
            Items = pagedResult.Items.Select(entity =>
            {
                var dto = MapToDto(entity);
                dto.AttachmentCount = attachmentCounts.GetValueOrDefault(entity.Id);
                return dto;
            }).ToList(),
            TotalCount = pagedResult.TotalCount,
            PageNumber = pagedResult.PageNumber,
            PageSize = pagedResult.PageSize
        };
    }

    /// <inheritdoc />
    public async Task<Dictionary<long, string?>> GetDocumentNumbersAsync(
        IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        return await _context.Invoice
            .ReceivedOnly()
            .AsNoTracking()
            .Where(r => ids.Contains(r.Id) && r.Status != EInvoiceStatus.Deleted)
            .Select(r => new { r.Id, r.DocumentNumber })
            .ToDictionaryAsync(x => x.Id, x => x.DocumentNumber, ct);
    }

    /// <inheritdoc />
    public async Task<ReceivedInvoiceDto?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        var entity = await BaseQuery()
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        return entity is null ? null : MapToDto(entity);
    }

    /// <inheritdoc />
    public async Task<ReceivedInvoiceDto> CreateAsync(CreateReceivedInvoiceDto dto, CancellationToken ct = default)
    {
        // The create rules live in ONE place (ReceivedDocumentWriter, shared with the unified document API);
        // this facade only translates the legacy DTO.
        var id = await _writer.CreateAsync(new CreateInvoiceDto
        {
            DocumentType = EDocumentType.ReceivedInvoice,
            ClientId = dto.SupplierId,
            CustomDocumentNumber = dto.DocumentNumber,
            IssueDate = dto.IssueDate,
            ReceivedDate = dto.ReceivedDate,
            DueDate = dto.DueDate,
            TaxableSupplyDate = dto.TaxableSupplyDate,
            VariableSymbol = dto.VariableSymbol,
            CurrencyId = dto.CurrencyId,
            ExchangeRate = dto.ExchangeRate,
            PaymentMethod = dto.PaymentMethod,
            BankAccountNumber = dto.BankAccountNumber,
            IBAN = dto.IBAN,
            SWIFT = dto.SWIFT,
            Notes = dto.Notes,
            InvoiceItem = ToUnifiedItems(dto.Items)
        }, ct);

        // Reload with includes to return full DTO
        return (await GetByIdAsync(id, ct))!;
    }

    /// <inheritdoc />
    public async Task<ReceivedInvoiceDto?> UpdateAsync(long id, UpdateReceivedInvoiceDto dto, CancellationToken ct = default)
    {
        var entity = await _context.Invoice
            .ReceivedOnly()
            .Include(r => r.InvoiceItem)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        if (entity is null) return null;

        // Same single rule set as the unified API (ReceivedDocumentWriter): status gate, exchange-rate rule, items.
        await _writer.UpdateAsync(entity, new UpdateInvoiceDto
        {
            DocumentNumber = dto.DocumentNumber,
            IssueDate = dto.IssueDate,
            ReceivedDate = dto.ReceivedDate,
            DueDate = dto.DueDate,
            TaxableSupplyDate = dto.TaxableSupplyDate,
            VariableSymbol = dto.VariableSymbol,
            CurrencyId = dto.CurrencyId,
            ExchangeRate = dto.ExchangeRate,
            PaymentMethod = dto.PaymentMethod,
            BankAccountNumber = dto.BankAccountNumber,
            IBAN = dto.IBAN,
            SWIFT = dto.SWIFT,
            Notes = dto.Notes,
            InvoiceItem = dto.Items is null ? null : ToUnifiedItems(dto.Items)
        }, ct);

        return await GetByIdAsync(id, ct);
    }

    /// <summary>Legacy received item DTO -> unified item DTO (the same fields; a received item is never a text row).</summary>
    private static List<CreateInvoiceItemDto> ToUnifiedItems(IEnumerable<CreateReceivedInvoiceItemDto> items) =>
        items.Select(i => new CreateInvoiceItemDto
        {
            OrderIndex = i.OrderIndex,
            Description = i.Description,
            Quantity = i.Quantity,
            Unit = i.Unit,
            UnitPrice = i.UnitPrice,
            VatRateId = i.VatRateId,
            VatRatePercentage = i.VatRatePercentage,
            VatRegime = i.VatRegime,
            ReverseChargeCodeId = i.ReverseChargeCodeId,
            ProductCode = i.ProductCode,
            Notes = i.Notes
        }).ToList();

    /// <inheritdoc />
    public async Task<ReceivedInvoiceDto?> ApproveAsync(long id, CancellationToken ct = default)
    {
        var entity = await _context.Invoice.ReceivedOnly().Include(r => r.Currency).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (entity is null) return null;

        // Only the wording of the refusal is received-specific (the contract speaks of Received / Approved);
        // the rule itself is the shared one below.
        if (entity.Status != EInvoiceStatus.Draft)
            throw new InvalidOperationException($"Cannot approve invoice in {ToLegacy(entity.Status)} status. Only Received allowed.");

        // "Approve" = the shared Draft -> Completed transition (status check + ČNB exchange rate).
        await _transitions.CompleteAsync(entity, issuedChecks: null, ct);

        _logger.LogInformation("Approved received invoice ID {Id}", id);
        return await GetByIdAsync(id, ct);
    }

    /// <inheritdoc />
    public async Task<ReceivedInvoiceDto?> MarkAsPaidAsync(long id, DateTime? paidAt = null, CancellationToken ct = default)
    {
        var entity = await _context.Invoice.ReceivedOnly().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (entity is null) return null;

        await _transitions.MarkPaidAsync(entity, paidAt, ct);

        _logger.LogInformation("Marked received invoice ID {Id} as paid", id);
        return await GetByIdAsync(id, ct);
    }

    /// <inheritdoc />
    public async Task<ReceivedInvoiceDto?> MarkAsUnpaidAsync(long id, CancellationToken ct = default)
    {
        var entity = await _transitions.MarkUnpaidAsync(_context.Invoice.ReceivedOnly(), id, ct);
        if (entity is null) return null;

        _logger.LogInformation("Marked received invoice ID {Id} as unpaid", id);
        return await GetByIdAsync(id, ct);
    }

    /// <inheritdoc />
    public async Task<ReceivedInvoiceDto?> RevertToDraftAsync(long id, CancellationToken ct = default)
    {
        var entity = await _context.Invoice.ReceivedOnly().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (entity is null) return null;

        await _transitions.RevertToDraftAsync(entity, ct);

        return await GetByIdAsync(id, ct);
    }

    /// <inheritdoc />
    public async Task<ReceivedInvoiceDto?> RestoreAsync(long id, CancellationToken ct = default)
    {
        var entity = await _context.Invoice.ReceivedOnly().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (entity is null) return null;

        await _transitions.RestoreAsync(entity, ct);

        return await GetByIdAsync(id, ct);
    }

    /// <inheritdoc />
    public async Task<ReceivedInvoiceDto?> RejectAsync(long id, CancellationToken ct = default)
    {
        var entity = await _context.Invoice.ReceivedOnly().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (entity is null) return null;

        var status = ToLegacy(entity.Status);
        if (status != EReceivedInvoiceStatus.Received)
            throw new InvalidOperationException($"Cannot reject invoice in {status} status. Only Received allowed.");

        // "Rejected" is abolished in the unified model: rejecting IS the shared Draft -> Deleted transition
        // (the check above only keeps the legacy wording of the refusal).
        await _transitions.DeleteAsync(entity, ct);

        _logger.LogInformation("Rejected (soft-deleted) received invoice ID {Id}", id);
        return await GetByIdAsync(id, ct);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        var entity = await _context.Invoice.ReceivedOnly().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (entity is null) return false;

        // Shared delete: Draft only for a received document (the supplier's number is left untouched).
        await _transitions.DeleteAsync(entity, ct);

        _logger.LogInformation("Soft-deleted received invoice ID {Id}", id);
        return true;
    }
}
