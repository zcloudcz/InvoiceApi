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
    private readonly IWebhookPublisher? _webhookPublisher;
    private readonly DocumentTransitions _transitions; // the ONE state machine shared with InvoiceService

    public ReceivedInvoiceService(TenantDbContext context, ILogger<ReceivedInvoiceService> logger, IWebhookPublisher? webhookPublisher = null,
        IExchangeRateService? exchangeRateService = null)
    {
        _context = context;
        _logger = logger;
        _webhookPublisher = webhookPublisher;
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
        _logger.LogInformation("Creating received invoice from supplier {SupplierId}", dto.SupplierId);

        // Validate supplier exists
        var supplier = await _context.Client
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == dto.SupplierId && c.IsActive, ct);

        if (supplier is null)
            throw new InvalidOperationException($"Supplier with ID {dto.SupplierId} not found or inactive.");

        ValidateReverseChargeCodes(dto.Items);

        // IssuerId is NOT NULL on Invoice: for a received document it is OUR company (the IsIssuer client).
        // Lowest Id wins - the same rule the data migration used for the existing rows.
        var issuerId = await _context.Client
            .AsNoTracking()
            .Where(c => c.IsIssuer)
            .OrderBy(c => c.Id)
            .Select(c => (long?)c.Id)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Tenant has no issuer company (Client with IsIssuer = true) configured.");

        // Create entity
        var entity = new Invoice
        {
            DocumentType = EDocumentType.ReceivedInvoice,
            DocumentNumber = dto.DocumentNumber,
            Status = EInvoiceStatus.Draft, // legacy "Received"
            ClientId = dto.SupplierId,
            IssuerId = issuerId,
            IssueDate = dto.IssueDate,
            ReceivedDate = dto.ReceivedDate ?? DateTime.UtcNow,
            DueDate = dto.DueDate,
            TaxableSupplyDate = dto.TaxableSupplyDate ?? dto.IssueDate,
            VariableSymbol = dto.VariableSymbol,
            CurrencyId = dto.CurrencyId,
            ExchangeRate = dto.ExchangeRate, // manual override (null = ČNB rate is assigned on approval)
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

            var item = new InvoiceItem
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

            entity.InvoiceItem.Add(item);
            totalBeforeVat += itemTotalBeforeVat;
            totalVat += item.VatAmount;
        }

        entity.TotalBeforeVat = totalBeforeVat;
        entity.TotalVat = totalVat;
        entity.TotalWithVat = totalBeforeVat + totalVat;

        _context.Invoice.Add(entity);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Created received invoice ID {Id} from supplier {SupplierId}, total {Total}",
            entity.Id, entity.ClientId, entity.TotalWithVat);

        // "received_invoice.created" webhook — never throws (see IWebhookPublisher).
        if (_webhookPublisher != null)
            await _webhookPublisher.PublishReceivedInvoiceEventAsync(
                Contracts.Dto.Webhook.WebhookEventCatalog.ReceivedInvoiceCreated, entity.Id, ct);

        // Reload with includes to return full DTO
        return (await GetByIdAsync(entity.Id, ct))!;
    }

    /// <inheritdoc />
    public async Task<ReceivedInvoiceDto?> UpdateAsync(long id, UpdateReceivedInvoiceDto dto, CancellationToken ct = default)
    {
        var entity = await _context.Invoice
            .ReceivedOnly()
            .Include(r => r.InvoiceItem)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        if (entity is null) return null;

        // Only allow updates in Received or Approved status (legacy vocabulary)
        var status = ToLegacy(entity.Status);
        if (status != EReceivedInvoiceStatus.Received &&
            status != EReceivedInvoiceStatus.Approved)
        {
            throw new InvalidOperationException(
                $"Cannot update received invoice in {status} status. Only Received or Approved allowed.");
        }

        // Update simple fields (only if provided)
        if (dto.DocumentNumber is not null) entity.DocumentNumber = dto.DocumentNumber;
        if (dto.IssueDate.HasValue) entity.IssueDate = dto.IssueDate;
        if (dto.ReceivedDate.HasValue) entity.ReceivedDate = dto.ReceivedDate;
        if (dto.DueDate.HasValue) entity.DueDate = dto.DueDate;
        if (dto.TaxableSupplyDate.HasValue) entity.TaxableSupplyDate = dto.TaxableSupplyDate;
        if (dto.VariableSymbol is not null) entity.VariableSymbol = dto.VariableSymbol;
        if (dto.CurrencyId.HasValue) entity.CurrencyId = dto.CurrencyId.Value;
        if (dto.ExchangeRate.HasValue)
        {
            // Like an issued invoice: the rate is fixed once the document is approved.
            if (status != EReceivedInvoiceStatus.Received)
                throw new InvalidOperationException("The exchange rate can be changed only before the invoice is approved.");
            entity.ExchangeRate = dto.ExchangeRate.Value;
            entity.ExchangeRateDate = null; // manual rate has no ČNB date
        }
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
            _context.InvoiceItem.RemoveRange(entity.InvoiceItem);

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

                var item = new InvoiceItem
                {
                    InvoiceId = entity.Id,
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

                _context.InvoiceItem.Add(item);
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
            // JSON clients (MCP/API) can send an undefined numeric enum value; reject it up front.
            if (!Enum.IsDefined(item.VatRegime))
                throw new InvalidOperationException(
                    $"Received invoice item '{item.Description}' has an invalid VatRegime value '{(int)item.VatRegime}'.");

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
    private static void CalculateItemVat(InvoiceItem item)
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
