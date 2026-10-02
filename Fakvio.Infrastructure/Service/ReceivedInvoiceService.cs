using Fakvio.Application.Common.Extensions;
using Fakvio.Contracts.Common.Pagination;
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
/// </summary>
public class ReceivedInvoiceService : IReceivedInvoiceService
{
    private readonly TenantDbContext _context;
    private readonly ILogger<ReceivedInvoiceService> _logger;

    public ReceivedInvoiceService(TenantDbContext context, ILogger<ReceivedInvoiceService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Maps a ReceivedInvoice entity to ReceivedInvoiceDto.
    /// ZMapper handles direct property mapping; navigation-derived props set manually.
    /// </summary>
    private static ReceivedInvoiceDto MapToDto(ReceivedInvoice entity)
    {
        var dto = entity.ToReceivedInvoiceDto();

        // Navigation-derived properties — ZMapper cannot flatten navigation paths
        dto.SupplierName = entity.Supplier?.CompanyName ?? string.Empty;
        dto.CurrencyCode = entity.Currency?.Code ?? string.Empty;
        dto.CurrencySymbol = entity.Currency?.Symbol ?? string.Empty;

        return dto;
    }

    /// <summary>
    /// Base query with all necessary includes for read operations.
    /// AsNoTracking for performance — no entity modifications during reads.
    /// </summary>
    private IQueryable<ReceivedInvoice> BaseQuery()
    {
        return _context.ReceivedInvoice
            .AsNoTracking()
            .Include(r => r.Supplier)
            .Include(r => r.Currency)
            .Include(r => r.Items.OrderBy(i => i.OrderIndex));
    }

    /// <inheritdoc />
    public async Task<List<ReceivedInvoiceDto>> GetAllAsync(
        EReceivedInvoiceStatus? status = null,
        long? supplierId = null,
        CancellationToken ct = default)
    {
        var query = BaseQuery()
            .Where(r => r.Status != EReceivedInvoiceStatus.Deleted);

        if (status.HasValue)
            query = query.Where(r => r.Status == status.Value);

        if (supplierId.HasValue)
            query = query.Where(r => r.SupplierId == supplierId.Value);

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
            .Where(r => r.Status != EReceivedInvoiceStatus.Deleted);

        // Apply filters
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.ToLower();
            query = query.Where(r =>
                (r.DocumentNumber != null && r.DocumentNumber.ToLower().Contains(search)) ||
                r.Supplier.CompanyName.ToLower().Contains(search) ||
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
            query = query.Where(r => r.Supplier.CompanyName.ToLower().Contains(supplierName));
        }

        if (filter.Status.HasValue)
            query = query.Where(r => r.Status == filter.Status.Value);

        if (filter.SupplierId.HasValue)
            query = query.Where(r => r.SupplierId == filter.SupplierId.Value);

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
            query = query.Where(r =>
                r.Status == EReceivedInvoiceStatus.Approved && r.DueDate < now);
        }

        if (!string.IsNullOrWhiteSpace(filter.Currency))
            query = query.Where(r => r.Currency.Code == filter.Currency);

        if (filter.MinAmount.HasValue)
            query = query.Where(r => r.TotalWithVat >= filter.MinAmount.Value);

        if (filter.MaxAmount.HasValue)
            query = query.Where(r => r.TotalWithVat <= filter.MaxAmount.Value);

        // Sorting — default by received date descending
        var sortBy = filter.SortBy?.ToLower() ?? "receiveddate";
        var isDesc = filter.SortDirection == null ? true : filter.IsDescending;

        query = sortBy switch
        {
            "documentnumber" => isDesc ? query.OrderByDescending(r => r.DocumentNumber) : query.OrderBy(r => r.DocumentNumber),
            "suppliername" => isDesc ? query.OrderByDescending(r => r.Supplier.CompanyName) : query.OrderBy(r => r.Supplier.CompanyName),
            "issuedate" => isDesc ? query.OrderByDescending(r => r.IssueDate) : query.OrderBy(r => r.IssueDate),
            "duedate" => isDesc ? query.OrderByDescending(r => r.DueDate) : query.OrderBy(r => r.DueDate),
            "totalwithvat" => isDesc ? query.OrderByDescending(r => r.TotalWithVat) : query.OrderBy(r => r.TotalWithVat),
            "status" => isDesc ? query.OrderByDescending(r => r.Status) : query.OrderBy(r => r.Status),
            _ => isDesc ? query.OrderByDescending(r => r.ReceivedDate) : query.OrderBy(r => r.ReceivedDate)
        };

        // Use the same paging extension as InvoiceService
        var pagedResult = await query.ToPagedResultAsync(filter.Page, filter.PageSize, ct);

        // Attachment counts for the grid's "download attachments" action.
        // One grouped query over just the page's ids (max PageSize=100 rows) —
        // avoids an N+1 per row while keeping the entity-based MapToDto untouched.
        var pageIds = pagedResult.Items.Select(r => r.Id).ToList();
        var attachmentCounts = await _context.FileAttachment
            .AsNoTracking()
            .Where(f => f.EntityName == nameof(ReceivedInvoice) && pageIds.Contains(f.RecordId))
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
        return await _context.ReceivedInvoice
            .AsNoTracking()
            .Where(r => ids.Contains(r.Id) && r.Status != EReceivedInvoiceStatus.Deleted)
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
        _logger.LogInformation("Creating received invoice from supplier {SupplierId}", dto.SupplierId);

        // Validate supplier exists
        var supplier = await _context.Client
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == dto.SupplierId && c.IsActive, ct);

        if (supplier is null)
            throw new InvalidOperationException($"Supplier with ID {dto.SupplierId} not found or inactive.");

        ValidateReverseChargeCodes(dto.Items);

        // Create entity
        var entity = new ReceivedInvoice
        {
            DocumentNumber = dto.DocumentNumber,
            Status = EReceivedInvoiceStatus.Received,
            SupplierId = dto.SupplierId,
            IssueDate = dto.IssueDate,
            ReceivedDate = dto.ReceivedDate ?? DateTime.UtcNow,
            DueDate = dto.DueDate,
            TaxableSupplyDate = dto.TaxableSupplyDate ?? dto.IssueDate,
            VariableSymbol = dto.VariableSymbol,
            CurrencyId = dto.CurrencyId,
            PaymentMethod = dto.PaymentMethod,
            BankAccountNumber = dto.BankAccountNumber,
            IBAN = dto.IBAN,
            SWIFT = dto.SWIFT,
            Notes = dto.Notes
        };

        // Process line items and calculate totals
        decimal totalBeforeVat = 0;
        decimal totalVat = 0;

        foreach (var itemDto in dto.Items)
        {
            var vatPercentage = itemDto.VatRatePercentage;

            // If VatRateId provided, fetch the actual percentage from DB
            if (itemDto.VatRateId.HasValue)
            {
                var vatRate = await _context.VatRate
                    .AsNoTracking()
                    .FirstOrDefaultAsync(v => v.Id == itemDto.VatRateId.Value, ct);

                if (vatRate is not null)
                    vatPercentage = vatRate.Rate;
            }

            var itemTotalBeforeVat = itemDto.Quantity * itemDto.UnitPrice;

            var item = new ReceivedInvoiceItem
            {
                OrderIndex = itemDto.OrderIndex,
                Description = itemDto.Description,
                Quantity = itemDto.Quantity,
                Unit = itemDto.Unit,
                UnitPrice = itemDto.UnitPrice,
                VatRateId = itemDto.VatRateId,
                VatRatePercentage = vatPercentage,
                TotalBeforeVat = itemTotalBeforeVat,
                VatRegime = itemDto.VatRegime,
                ReverseChargeCodeId = itemDto.ReverseChargeCodeId,
                ProductCode = itemDto.ProductCode,
                Notes = itemDto.Notes
            };
            CalculateItemVat(item);

            entity.Items.Add(item);
            totalBeforeVat += itemTotalBeforeVat;
            totalVat += item.VatAmount;
        }

        entity.TotalBeforeVat = totalBeforeVat;
        entity.TotalVat = totalVat;
        entity.TotalWithVat = totalBeforeVat + totalVat;

        _context.ReceivedInvoice.Add(entity);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Created received invoice ID {Id} from supplier {SupplierId}, total {Total}",
            entity.Id, entity.SupplierId, entity.TotalWithVat);

        // Reload with includes to return full DTO
        return (await GetByIdAsync(entity.Id, ct))!;
    }

    /// <inheritdoc />
    public async Task<ReceivedInvoiceDto?> UpdateAsync(long id, UpdateReceivedInvoiceDto dto, CancellationToken ct = default)
    {
        var entity = await _context.ReceivedInvoice
            .Include(r => r.Items)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        if (entity is null) return null;

        // Only allow updates in Received or Approved status
        if (entity.Status != EReceivedInvoiceStatus.Received &&
            entity.Status != EReceivedInvoiceStatus.Approved)
        {
            throw new InvalidOperationException(
                $"Cannot update received invoice in {entity.Status} status. Only Received or Approved allowed.");
        }

        // Update simple fields (only if provided)
        if (dto.DocumentNumber is not null) entity.DocumentNumber = dto.DocumentNumber;
        if (dto.IssueDate.HasValue) entity.IssueDate = dto.IssueDate;
        if (dto.ReceivedDate.HasValue) entity.ReceivedDate = dto.ReceivedDate;
        if (dto.DueDate.HasValue) entity.DueDate = dto.DueDate;
        if (dto.TaxableSupplyDate.HasValue) entity.TaxableSupplyDate = dto.TaxableSupplyDate;
        if (dto.VariableSymbol is not null) entity.VariableSymbol = dto.VariableSymbol;
        if (dto.CurrencyId.HasValue) entity.CurrencyId = dto.CurrencyId.Value;
        if (dto.PaymentMethod.HasValue) entity.PaymentMethod = dto.PaymentMethod;
        if (dto.BankAccountNumber is not null) entity.BankAccountNumber = dto.BankAccountNumber;
        if (dto.IBAN is not null) entity.IBAN = dto.IBAN;
        if (dto.SWIFT is not null) entity.SWIFT = dto.SWIFT;
        if (dto.Notes is not null) entity.Notes = dto.Notes;

        // If items provided, replace all existing items
        if (dto.Items is not null)
        {
            ValidateReverseChargeCodes(dto.Items);

            // Remove old items
            _context.ReceivedInvoiceItem.RemoveRange(entity.Items);

            // Add new items and recalculate totals
            decimal totalBeforeVat = 0;
            decimal totalVat = 0;

            foreach (var itemDto in dto.Items)
            {
                var vatPercentage = itemDto.VatRatePercentage;

                if (itemDto.VatRateId.HasValue)
                {
                    var vatRate = await _context.VatRate
                        .AsNoTracking()
                        .FirstOrDefaultAsync(v => v.Id == itemDto.VatRateId.Value, ct);

                    if (vatRate is not null)
                        vatPercentage = vatRate.Rate;
                }

                var itemTotalBeforeVat = itemDto.Quantity * itemDto.UnitPrice;

                var item = new ReceivedInvoiceItem
                {
                    ReceivedInvoiceId = entity.Id,
                    OrderIndex = itemDto.OrderIndex,
                    Description = itemDto.Description,
                    Quantity = itemDto.Quantity,
                    Unit = itemDto.Unit,
                    UnitPrice = itemDto.UnitPrice,
                    VatRateId = itemDto.VatRateId,
                    VatRatePercentage = vatPercentage,
                    TotalBeforeVat = itemTotalBeforeVat,
                    VatRegime = itemDto.VatRegime,
                    ReverseChargeCodeId = itemDto.ReverseChargeCodeId,
                    ProductCode = itemDto.ProductCode,
                    Notes = itemDto.Notes
                };
                CalculateItemVat(item);

                _context.ReceivedInvoiceItem.Add(item);
                totalBeforeVat += itemTotalBeforeVat;
                totalVat += item.VatAmount;
            }

            entity.TotalBeforeVat = totalBeforeVat;
            entity.TotalVat = totalVat;
            entity.TotalWithVat = totalBeforeVat + totalVat;
        }

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Updated received invoice ID {Id}", id);
        return await GetByIdAsync(id, ct);
    }

    /// <inheritdoc />
    public async Task<ReceivedInvoiceDto?> ApproveAsync(long id, CancellationToken ct = default)
    {
        var entity = await _context.ReceivedInvoice.FindAsync(new object[] { id }, ct);
        if (entity is null) return null;

        if (entity.Status != EReceivedInvoiceStatus.Received)
            throw new InvalidOperationException($"Cannot approve invoice in {entity.Status} status. Only Received allowed.");

        entity.Status = EReceivedInvoiceStatus.Approved;
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Approved received invoice ID {Id}", id);
        return await GetByIdAsync(id, ct);
    }

    /// <inheritdoc />
    public async Task<ReceivedInvoiceDto?> MarkAsPaidAsync(long id, DateTime? paidAt = null, CancellationToken ct = default)
    {
        var entity = await _context.ReceivedInvoice.FindAsync(new object[] { id }, ct);
        if (entity is null) return null;

        if (entity.Status != EReceivedInvoiceStatus.Approved)
            throw new InvalidOperationException($"Cannot mark as paid in {entity.Status} status. Only Approved allowed.");

        entity.Status = EReceivedInvoiceStatus.Paid;
        entity.PaidAt = paidAt ?? DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Marked received invoice ID {Id} as paid", id);
        return await GetByIdAsync(id, ct);
    }

    /// <inheritdoc />
    public async Task<ReceivedInvoiceDto?> RejectAsync(long id, CancellationToken ct = default)
    {
        var entity = await _context.ReceivedInvoice.FindAsync(new object[] { id }, ct);
        if (entity is null) return null;

        if (entity.Status != EReceivedInvoiceStatus.Received)
            throw new InvalidOperationException($"Cannot reject invoice in {entity.Status} status. Only Received allowed.");

        entity.Status = EReceivedInvoiceStatus.Rejected;
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Rejected received invoice ID {Id}", id);
        return await GetByIdAsync(id, ct);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        var entity = await _context.ReceivedInvoice.FindAsync(new object[] { id }, ct);
        if (entity is null) return false;

        if (entity.Status != EReceivedInvoiceStatus.Received &&
            entity.Status != EReceivedInvoiceStatus.Rejected)
        {
            throw new InvalidOperationException(
                $"Cannot delete invoice in {entity.Status} status. Only Received or Rejected allowed.");
        }

        entity.Status = EReceivedInvoiceStatus.Deleted;
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Soft-deleted received invoice ID {Id}", id);
        return true;
    }

    // ─── Private Helpers — Reverse Charge (mirrors InvoiceService) ─────────────

    /// <summary>
    /// Validates Reverse Charge code consistency for a list of item DTOs.
    /// Same rules as InvoiceService.ValidateReverseChargeCodes (issue epo-vat-reporting):
    /// 1. VatRegime == ReverseCharge requires ReverseChargeCodeId to be set.
    /// 2. Any other regime requires ReverseChargeCodeId to be null.
    /// </summary>
    private static void ValidateReverseChargeCodes(IEnumerable<CreateReceivedInvoiceItemDto> items)
    {
        foreach (var item in items)
        {
            if (item.VatRegime == EVatRegime.ReverseCharge && !item.ReverseChargeCodeId.HasValue)
                throw new InvalidOperationException(
                    $"Received invoice item '{item.Description}' has VatRegime=ReverseCharge but no ReverseChargeCodeId. " +
                    "A reverse charge code (kód předmětu plnění) is required for PDP items.");

            if (item.VatRegime != EVatRegime.ReverseCharge && item.ReverseChargeCodeId.HasValue)
                throw new InvalidOperationException(
                    $"Received invoice item '{item.Description}' has ReverseChargeCodeId set but VatRegime={item.VatRegime}. " +
                    "ReverseChargeCodeId must only be set for ReverseCharge items.");
        }
    }

    /// <summary>
    /// Calculates VatAmount, InformationalVatAmount, and TotalWithVat for a received invoice
    /// item, applying the correct logic per the item's VatRegime.
    ///
    /// Unlike the issued-invoice side, "VatAmount" here is money WE actually owe to the
    /// supplier — for ReverseCharge it stays 0 because the supplier's invoice itself does
    /// not bill VAT. InformationalVatAmount instead holds the self-assessed amount we must
    /// declare as both output tax (we owe it) and input tax (we may deduct it) — see
    /// VatReportService rows 10/11 and 43/44 of DPHDP3.
    ///
    /// TotalBeforeVat must be set on the item before calling this method.
    /// </summary>
    private static void CalculateItemVat(ReceivedInvoiceItem item)
    {
        switch (item.VatRegime)
        {
            case EVatRegime.Standard:
                item.VatAmount = Math.Round(
                    item.TotalBeforeVat * (item.VatRatePercentage / 100m),
                    2, MidpointRounding.AwayFromZero);
                item.TotalWithVat = item.TotalBeforeVat + item.VatAmount;
                item.InformationalVatAmount = 0;
                break;

            case EVatRegime.ReverseCharge:
                // PDP: supplier billed 0 VAT — WE self-assess it (§92a ZDPH). A 0 % rate would
                // silently drop the supply from the VAT filings, so reject it.
                if (item.VatRatePercentage <= 0m)
                    throw new InvalidOperationException(
                        $"Received invoice item '{item.Description}' uses reverse charge, so it needs the VAT rate that applies " +
                        "in the Czech Republic (VatRatePercentage > 0).");
                item.VatAmount = 0;
                item.TotalWithVat = item.TotalBeforeVat;
                item.InformationalVatAmount = Math.Round(
                    item.TotalBeforeVat * (item.VatRatePercentage / 100m),
                    2, MidpointRounding.AwayFromZero);
                break;

            case EVatRegime.Exempt:
            case EVatRegime.OutOfScope:
                item.VatAmount = 0;
                item.TotalWithVat = item.TotalBeforeVat;
                item.InformationalVatAmount = 0;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(item.VatRegime),
                    item.VatRegime, "Unhandled VatRegime value in CalculateItemVat.");
        }
    }
}
