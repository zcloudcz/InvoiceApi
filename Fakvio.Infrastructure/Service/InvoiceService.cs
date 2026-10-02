using Fakvio.Contracts.Common;
using Fakvio.Application.Common.Extensions;
using Fakvio.Application.Exceptions;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZMapper;
using Fakvio.Contracts.Dto.ReverseChargeCode;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Implementation of invoice service
/// Handles all business logic for invoices and credit notes
/// </summary>
public class InvoiceService : IInvoiceService
{
    private readonly TenantDbContext _context;
    private readonly INumberSequenceService _numberSequenceService;
    private readonly ITenantReadinessService _tenantReadinessService;
    private readonly ILogger<InvoiceService> _logger;

    public InvoiceService(
        TenantDbContext context,
        INumberSequenceService numberSequenceService,
        ITenantReadinessService tenantReadinessService,
        ILogger<InvoiceService> logger)
    {
        _context = context;
        _numberSequenceService = numberSequenceService;
        _tenantReadinessService = tenantReadinessService;
        _logger = logger;
    }

    /// <summary>
    /// Maps an Invoice entity to InvoiceDto using ZMapper v1.1.0.
    /// ZMapper handles BaseEntity (Id, CreatedAt, UpdatedAt) and scalar properties automatically.
    /// Navigation-derived properties (flattened from related entities) and nested navigation
    /// objects (ReverseChargeCode on items) must be set manually after ZMapper runs.
    ///
    /// Prerequisite: the caller must have eagerly loaded InvoiceItem.ReverseChargeCode via
    /// ThenInclude so that the navigation property is populated before this mapper runs.
    /// </summary>
    private static InvoiceDto MapToDto(Invoice entity)
    {
        var dto = entity.ToInvoiceDto();

        // Navigation-derived properties — ZMapper cannot flatten navigation paths
        // (e.g., entity.Client.CompanyName → dto.ClientName) so we set them manually.
        dto.ClientName = entity.Client?.CompanyName ?? string.Empty;
        dto.ClientColor = entity.Client?.Color;
        dto.IssuerName = entity.Issuer?.CompanyName ?? string.Empty;
        dto.CurrencyCode = entity.Currency?.Code ?? string.Empty;
        dto.CurrencySymbol = entity.Currency?.Symbol ?? string.Empty;
        dto.OriginalInvoiceNumber = entity.OriginalInvoice?.DocumentNumber;

        // Populate the nested ReverseChargeCode on every line item. ZMapper copies scalar
        // properties only, so the navigation object has to be mapped by hand here.
        //
        // Items are paired by Id, not by list position. Pairing by position would also be
        // correct today — the generated ZMapper builds dto.InvoiceItem as
        // source.InvoiceItem.Select(...).ToList(), a 1:1 order-preserving projection of the
        // same collection — but that makes correctness here depend on a detail of generated
        // code that nothing in this file controls. Keying on the primary key removes the
        // coupling at identical O(n) cost. Note the guarantee is only as strong as the
        // pairing itself: no test can distinguish the two variants from outside MapToDto.
        //
        // The Any() pre-check keeps the common case (an invoice with no reverse-charge line)
        // free of the dictionary allocation — it is a cheap O(n) scan that allocates nothing.
        if (dto.InvoiceItem.Count > 0 && entity.InvoiceItem.Any(item => item.ReverseChargeCode is not null))
        {
            var dtoItemsById = dto.InvoiceItem.ToDictionary(item => item.Id);

            foreach (var entityItem in entity.InvoiceItem)
            {
                if (entityItem.ReverseChargeCode is null)
                {
                    continue;
                }

                // Unknown Id cannot happen for a DB-loaded graph; TryGetValue just avoids a
                // throw if one ever did. It is not a general robustness guarantee — the
                // ToDictionary above already requires the ids to be unique and would throw
                // first on a graph of unsaved items that all still sit at Id == 0. Every
                // caller of MapToDto reads through AsNoTracking with real primary keys, so
                // that case is unreachable rather than handled.
                if (dtoItemsById.TryGetValue(entityItem.Id, out var itemDto))
                {
                    itemDto.ReverseChargeCode = entityItem.ReverseChargeCode.ToReverseChargeCodeDto();
                }
            }
        }

        return dto;
    }

    public async Task<List<InvoiceDto>> GetAllInvoicesAsync(
        EDocumentType? documentType = null,
        EInvoiceStatus? status = null,
        long? clientId = null,
        long? issuerId = null,
        CancellationToken cancellationToken = default)
    {
        // AsNoTracking: read-only query — no entity modifications needed
        var query = _context.Invoice
            .AsNoTracking()
            .Include(i => i.Client)
            .Include(i => i.Issuer)
            .Include(i => i.Currency)
            .Include(i => i.InvoiceItem.OrderBy(item => item.OrderIndex))
                // Eagerly load ReverseChargeCode so items with VatRegime == ReverseCharge
                // have their navigation property populated. The FK is nullable, so EF Core
                // generates a LEFT JOIN — rows without a code (Standard/Exempt) get null here
                // and are returned normally without crashing.
                .ThenInclude(item => item.ReverseChargeCode)
            .Include(i => i.OriginalInvoice)
            .Where(i => i.Status != EInvoiceStatus.Deleted)
            .AsQueryable();

        // Apply filters
        if (documentType.HasValue)
            query = query.Where(i => i.DocumentType == documentType.Value);

        if (status.HasValue)
            query = query.Where(i => i.Status == status.Value);

        if (clientId.HasValue)
            query = query.Where(i => i.ClientId == clientId.Value);

        if (issuerId.HasValue)
            query = query.Where(i => i.IssuerId == issuerId.Value);

        var invoices = await query
            .OrderByDescending(i => i.IssueDate ?? DateTime.MinValue)
            .ThenByDescending(i => i.Id)
            .ToListAsync(cancellationToken);

        return invoices.Select(i => MapToDto(i)).ToList();
    }

    public async Task<PagedResult<InvoiceDto>> GetInvoicesPagedAsync(
        InvoiceFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching paged invoices (Page: {Page}, PageSize: {PageSize}, Search: {Search})",
            filter.Page, filter.PageSize, filter.Search);

        // AsNoTracking: read-only paged query — results are mapped to DTOs
        var query = _context.Invoice
            .AsNoTracking()
            .Include(i => i.Client)
            .Include(i => i.Issuer)
            .Include(i => i.Currency)
            .Include(i => i.InvoiceItem.OrderBy(item => item.OrderIndex))
                // Eagerly load ReverseChargeCode so items with VatRegime == ReverseCharge
                // have their navigation property populated. FK is nullable → LEFT JOIN, so
                // Standard/Exempt/OutOfScope items (ReverseChargeCodeId = null) are returned normally.
                .ThenInclude(item => item.ReverseChargeCode)
            .Include(i => i.OriginalInvoice)
            .AsQueryable();

        // Apply filters
        if (filter.DocumentType.HasValue)
            query = query.Where(i => i.DocumentType == filter.DocumentType.Value);

        // Only hide deleted invoices when no specific status filter is set.
        // When user explicitly selects "Deleted" status, show them.
        if (filter.Status.HasValue)
            query = query.Where(i => i.Status == filter.Status.Value);
        else
            query = query.Where(i => i.Status != EInvoiceStatus.Deleted);

        if (filter.ClientId.HasValue)
            query = query.Where(i => i.ClientId == filter.ClientId.Value);

        if (filter.IssuerId.HasValue)
            query = query.Where(i => i.IssuerId == filter.IssuerId.Value);

        // Date range filters — ensure UTC Kind for PostgreSQL 'timestamp with time zone' columns.
        // Dates from query string binding arrive with Kind=Unspecified which Npgsql rejects.
        if (filter.IssueDateFrom.HasValue)
            query = query.Where(i => i.IssueDate >= DateTime.SpecifyKind(filter.IssueDateFrom.Value, DateTimeKind.Utc));

        if (filter.IssueDateTo.HasValue)
            query = query.Where(i => i.IssueDate <= DateTime.SpecifyKind(filter.IssueDateTo.Value, DateTimeKind.Utc));

        if (filter.DueDateFrom.HasValue)
            query = query.Where(i => i.DueDate >= DateTime.SpecifyKind(filter.DueDateFrom.Value, DateTimeKind.Utc));

        if (filter.DueDateTo.HasValue)
            query = query.Where(i => i.DueDate <= DateTime.SpecifyKind(filter.DueDateTo.Value, DateTimeKind.Utc));

        if (filter.TaxableSupplyDateFrom.HasValue)
            query = query.Where(i => i.TaxableSupplyDate >= DateTime.SpecifyKind(filter.TaxableSupplyDateFrom.Value, DateTimeKind.Utc));

        if (filter.TaxableSupplyDateTo.HasValue)
            query = query.Where(i => i.TaxableSupplyDate <= DateTime.SpecifyKind(filter.TaxableSupplyDateTo.Value, DateTimeKind.Utc));

        // Overdue filter
        if (filter.IsOverdue.HasValue && filter.IsOverdue.Value)
        {
            var now = DateTime.UtcNow;
            query = query.Where(i => i.DueDate < now && i.Status != EInvoiceStatus.Paid);
        }

        // Currency filter
        if (!string.IsNullOrWhiteSpace(filter.Currency))
            query = query.Where(i => i.Currency != null && i.Currency.Code.ToLower() == filter.Currency.ToLower());

        // Amount range filters
        if (filter.MinAmount.HasValue)
            query = query.Where(i => i.TotalWithVat >= filter.MinAmount.Value);

        if (filter.MaxAmount.HasValue)
            query = query.Where(i => i.TotalWithVat <= filter.MaxAmount.Value);

        // Column filters — single-column contains, separate from the global Search below
        if (!string.IsNullOrWhiteSpace(filter.DocumentNumber))
        {
            var docNumber = filter.DocumentNumber.ToLower();
            query = query.Where(i => i.DocumentNumber != null && i.DocumentNumber.ToLower().Contains(docNumber));
        }

        if (!string.IsNullOrWhiteSpace(filter.ClientName))
        {
            var clientName = filter.ClientName.ToLower();
            query = query.Where(i => i.Client.CompanyName != null && i.Client.CompanyName.ToLower().Contains(clientName));
        }

        // Search filter - search across DocumentNumber, Client.CompanyName, VariableSymbol
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.ToLower();
            query = query.Where(i =>
                (i.DocumentNumber != null && i.DocumentNumber.ToLower().Contains(search)) ||
                (i.Client.CompanyName != null && i.Client.CompanyName.ToLower().Contains(search)) ||
                (i.VariableSymbol != null && i.VariableSymbol.Contains(search)));
        }

        // Apply sorting
        // Default sort (no or unknown SortBy): newest first by IssueDate. Not by DocumentNumber —
        // it is a string and a tenant can run several number series ("2582026017" vs "2026016"),
        // so a text sort pushes a whole series to the end regardless of date.
        // ThenByDescending(Id) breaks ties so paging stays stable when many invoices share
        // one issue date (month-end billing). "?? DateTime.MinValue" keeps invoices without
        // IssueDate last on PostgreSQL too (it puts NULLs first on DESC) — same as GetInvoicesAsync.
        var validSortFields = new[] { "DocumentNumber", "IssueDate", "DueDate", "TaxableSupplyDate", "TotalWithVat", "Status", "CreatedAt", "UpdatedAt" };
        var hasSortField = !string.IsNullOrWhiteSpace(filter.SortBy) && validSortFields.Contains(filter.SortBy, StringComparer.OrdinalIgnoreCase);

        query = hasSortField
            ? query.ApplySorting(filter.SortBy, filter.IsDescending)
            : query.OrderByDescending(i => i.IssueDate ?? DateTime.MinValue).ThenByDescending(i => i.Id);

        // Get paged results
        var pagedResult = await query.ToPagedResultAsync(filter.Page, filter.PageSize, cancellationToken);

        // Map to DTOs
        return new PagedResult<InvoiceDto>(
            pagedResult.Items.Select(i => MapToDto(i)).ToList(),
            pagedResult.TotalCount,
            pagedResult.PageNumber,
            pagedResult.PageSize);
    }

    public async Task<InvoiceDto?> GetInvoiceByIdAsync(long invoiceId, CancellationToken cancellationToken = default)
    {
        // AsNoTracking: read-only lookup — result is mapped to DTO
        var invoice = await _context.Invoice
            .AsNoTracking()
            .Include(i => i.Client)
            .Include(i => i.Issuer)
            .Include(i => i.Currency)
            .Include(i => i.InvoiceItem.OrderBy(item => item.OrderIndex))
                // Eagerly load ReverseChargeCode so PDP items have their nav prop populated.
                // FK is nullable → LEFT JOIN → Standard items return null here without crashing.
                .ThenInclude(item => item.ReverseChargeCode)
            .Include(i => i.OriginalInvoice)
            // Allow loading deleted invoices so users can view details and restore them.
            // List endpoints (GetAll, GetPaged) still hide deleted invoices by default.
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);

        return invoice == null ? null : MapToDto(invoice);
    }

    public async Task<InvoiceDto?> GetInvoiceByDocumentNumberAsync(string documentNumber, CancellationToken cancellationToken = default)
    {
        // AsNoTracking: read-only lookup — result is mapped to DTO
        var invoice = await _context.Invoice
            .AsNoTracking()
            .Include(i => i.Client)
            .Include(i => i.Issuer)
            .Include(i => i.Currency)
            .Include(i => i.InvoiceItem.OrderBy(item => item.OrderIndex))
                // Eagerly load ReverseChargeCode so PDP items have their nav prop populated.
                // FK is nullable → LEFT JOIN → Standard items return null here without crashing.
                .ThenInclude(item => item.ReverseChargeCode)
            .Include(i => i.OriginalInvoice)
            // Allow loading deleted invoices so users can view details and restore them.
            // List endpoints (GetAll, GetPaged) still hide deleted invoices by default.
            .FirstOrDefaultAsync(i => i.DocumentNumber == documentNumber, cancellationToken);

        return invoice == null ? null : MapToDto(invoice);
    }

    public Task<InvoiceDto> CreateInvoiceAsync(CreateInvoiceDto createDto, CancellationToken cancellationToken = default)
        // Public entry point always applies the bank-account default fill (issue: MCP-created
        // invoices had no bank account at all). Internal re-use of this pipeline for
        // proforma→final and invoice-copy passes applyBankAccountDefaulting:false — those two
        // explicitly copy the source document's bank fields (even when the source has none) and
        // must not have the resolver silently invent one.
        => CreateInvoiceCoreAsync(createDto, applyBankAccountDefaulting: true, cancellationToken);

    public Task<InvoiceDto> CreateImportedInvoiceAsync(CreateInvoiceDto createDto, CancellationToken cancellationToken = default)
        => CreateInvoiceCoreAsync(createDto, applyBankAccountDefaulting: false, cancellationToken);

    private async Task<InvoiceDto> CreateInvoiceCoreAsync(
        CreateInvoiceDto createDto, bool applyBankAccountDefaulting, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating new {DocumentType}", createDto.DocumentType);

        // Validate client and issuer exist
        var client = await _context.Client.FindAsync(new object[] { createDto.ClientId }, cancellationToken);
        if (client == null)
            throw new InvalidOperationException($"Client with ID {createDto.ClientId} not found");

        var issuer = await _context.Client.FindAsync(new object[] { createDto.IssuerId }, cancellationToken);
        if (issuer == null || !issuer.IsIssuer)
            throw new InvalidOperationException($"Issuer with ID {createDto.IssuerId} not found or is not marked as issuer");

        // For credit notes, validate original invoice exists
        if (createDto.DocumentType == EDocumentType.CreditNote)
        {
            if (!createDto.OriginalInvoiceId.HasValue)
                throw new InvalidOperationException("OriginalInvoiceId is required for credit notes");

            var originalInvoice = await _context.Invoice.FindAsync(new object[] { createDto.OriginalInvoiceId.Value }, cancellationToken);
            if (originalInvoice == null)
                throw new InvalidOperationException($"Original invoice with ID {createDto.OriginalInvoiceId} not found");

            if (originalInvoice.DocumentType != EDocumentType.Invoice)
                throw new InvalidOperationException("Credit notes can only be created for invoices, not other credit notes");
        }

        // Pre-save VariableSymbol duplicate check.
        // Only runs when the caller has explicitly opted in to a manual VS override
        // (VariableSymbolIsManualOverride = true). When the flag is false the VS
        // will be re-derived from the generated DocumentNumber below, so checking the
        // stale preview value sent by the UI would either produce a false positive
        // (flagging a preview digit string that is about to be discarded) or silently
        // skip a real collision — neither is correct. The post-generation duplicate
        // check further down handles the auto-derived value correctly.
        if (createDto.VariableSymbolIsManualOverride && !string.IsNullOrEmpty(createDto.VariableSymbol))
        {
            var preDuplicateExists = await _context.Invoice
                .AsNoTracking()
                .AnyAsync(i => i.VariableSymbol == createDto.VariableSymbol
                    && i.Status != EInvoiceStatus.Deleted,
                    cancellationToken);

            if (preDuplicateExists)
            {
                _logger.LogWarning(
                    "Duplicate VariableSymbol '{VS}' rejected before save", createDto.VariableSymbol);
                throw new InvalidOperationException(
                    $"An invoice with Variable Symbol '{createDto.VariableSymbol}' already exists. " +
                    "Each invoice must have a unique Variable Symbol for payment tracking.");
            }
        }

        // Create invoice entity
        var invoice = new Invoice
        {
            DocumentType = createDto.DocumentType,
            Status = EInvoiceStatus.Draft,
            DocumentNumber = createDto.CustomDocumentNumber ?? "DRAFT", // Placeholder — replaced below after save
            // NormalizeToUtcMidnight: date-only fields must be stored as UTC midnight.
            // Blazor WASM may send dates with local timezone offset (e.g., "2026-05-01T00:00:00+02:00")
            // which System.Text.Json deserializes as "2026-04-30T22:00:00Z" — the PREVIOUS day in UTC.
            // Stripping time via .Date and stamping as UTC ensures the calendar date is always preserved.
            IssueDate = NormalizeToUtcMidnight(createDto.IssueDate) ?? DateTime.UtcNow.Date,
            TaxableSupplyDate = NormalizeToUtcMidnight(createDto.TaxableSupplyDate)
                                ?? NormalizeToUtcMidnight(createDto.IssueDate)
                                ?? DateTime.UtcNow.Date,
            ClientId = createDto.ClientId,
            IssuerId = createDto.IssuerId,
            OriginalInvoiceId = createDto.OriginalInvoiceId,
            VariableSymbol = createDto.VariableSymbol,
            ConstantSymbol = createDto.ConstantSymbol,
            SpecificSymbol = createDto.SpecificSymbol,
            BankAccountNumber = createDto.BankAccountNumber,
            IBAN = createDto.IBAN,
            SWIFT = createDto.SWIFT,
            PaymentMethod = createDto.PaymentMethod,
            CurrencyId = createDto.CurrencyId,
            Notes = createDto.Notes,
            InvoiceItem = new List<InvoiceItem>()
        };

        // Resolve BankAccountId (if given) or auto-fill from the issuer's accounts when nothing
        // was supplied at all. See ApplyBankAccountDefaultsAsync for the exact rule.
        await ApplyBankAccountDefaultsAsync(
            invoice, createDto.IssuerId, createDto.CurrencyId, createDto.BankAccountId,
            applyBankAccountDefaulting, cancellationToken);

        // Calculate due date using the client's billing settings (respects DueDateCalculationType)
        invoice.DueDate = CalculateDueDate(createDto, client);

        // Sync TaxableSupplyDate (DUZP) with IssueDate —
        // Czech accounting: tax date (datum zdanitelného plnění) = issue date (datum vystavení).
        // Only auto-sync when user did NOT explicitly provide a TaxableSupplyDate.
        if (!createDto.TaxableSupplyDate.HasValue)
        {
            invoice.TaxableSupplyDate = invoice.IssueDate;
        }

        // Validate VAT requirements: If issuer is VAT payer, all items must have VatRateId
        if (issuer.IsVatPayer)
        {
            var itemsWithoutVatRate = createDto.InvoiceItem.Where(i => !i.IsTextRow && !i.VatRateId.HasValue).ToList();
            if (itemsWithoutVatRate.Any())
            {
                throw new InvalidOperationException(
                    "When issuer is a VAT payer, all invoice items must have a VAT rate assigned (VatRateId). " +
                    $"Found {itemsWithoutVatRate.Count} item(s) without VAT rate.");
            }
        }
        else
        {
            // Non-VAT payer: never charge VAT, whatever the caller sent (see NonVatPayerItems).
            NonVatPayerItems.StripVat(createDto.InvoiceItem);
        }

        // Validate Reverse Charge rules:
        // - ReverseCharge items MUST have ReverseChargeCodeId (identifies the type of supply for EPO).
        // - Non-ReverseCharge items MUST NOT have ReverseChargeCodeId (cannot mix regimes on one item).
        ValidateReverseChargeCodes(createDto.InvoiceItem);

        // Add invoice items and calculate totals
        decimal totalBeforeVat = 0;
        decimal totalVat = 0;

        foreach (var itemDto in createDto.InvoiceItem)
        {
            var item = new InvoiceItem
            {
                OrderIndex = itemDto.OrderIndex,
                IsTextRow = itemDto.IsTextRow,
                Description = itemDto.Description,
                ProductCode = itemDto.ProductCode,
                Notes = itemDto.Notes
            };

            // Text rows are display-only notes — no quantity, price, or VAT calculation.
            if (itemDto.IsTextRow)
            {
                item.Quantity = 0;
                item.UnitPrice = 0;
                item.Unit = "";
                invoice.InvoiceItem.Add(item);
                continue;
            }

            // Regular billable item — resolve VAT and calculate totals.
            // Note: VAT payers are guaranteed to have VatRateId set (validated above).
            // Non-VAT payers may omit VatRateId and use VatRatePercentage = 0 from the DTO instead.
            decimal vatRatePercentage = itemDto.VatRatePercentage;
            if (itemDto.VatRateId.HasValue)
            {
                var vatRate = await _context.VatRate.FindAsync(new object[] { itemDto.VatRateId.Value }, cancellationToken);
                if (vatRate == null)
                    throw new InvalidOperationException($"VAT rate with ID {itemDto.VatRateId} not found");
                vatRatePercentage = vatRate.Rate;
            }

            item.Quantity = itemDto.Quantity;
            item.Unit = itemDto.Unit;
            item.UnitPrice = itemDto.UnitPrice;
            item.VatRateId = itemDto.VatRateId;
            item.VatRatePercentage = vatRatePercentage;
            item.VatRegime = itemDto.VatRegime;
            item.ReverseChargeCodeId = itemDto.ReverseChargeCodeId;

            item.TotalBeforeVat = item.Quantity * item.UnitPrice;
            // Round VatAmount to 2 decimal places (AwayFromZero = standard Czech VAT rounding).
            // Without this, back-calculated deduction rows accumulate ~0.005 CZK drift per row
            // because deductionBase = round(deductionWithVat / divisor, 2) loses a fraction
            // that re-appears when VAT is recomputed from the rounded base.
            CalculateItemVat(item);

            invoice.InvoiceItem.Add(item);

            totalBeforeVat += item.TotalBeforeVat;
            // Only Standard regime items contribute to billed VAT.
            // ReverseCharge VAT is self-assessed by the buyer and never appears in the invoice total.
            // Exempt and OutOfScope have no VAT at all.
            totalVat += item.VatAmount;
        }

        invoice.TotalBeforeVat = totalBeforeVat;
        invoice.TotalVat = totalVat;
        invoice.TotalWithVat = totalBeforeVat + totalVat;

        _context.Invoice.Add(invoice);
        await _context.SaveChangesAsync(cancellationToken);

        // Generate the document number immediately at creation (not at completion)
        // so the user sees the real number straight away.
        // If a NumberSequenceId override was provided (e.g., from a template), pass it through.
        if (invoice.DocumentNumber == "DRAFT")
        {
            invoice.DocumentNumber = await GenerateDocumentNumberAsync(
                invoice, cancellationToken, createDto.NumberSequenceId);
        }

        // Derive VariableSymbol from the (now fully resolved) document number.
        // Czech banking: VS must be digits only, max 10 characters.
        //
        // Two cases:
        //   1) VariableSymbolIsManualOverride = true  → user deliberately typed a custom VS
        //      (e.g. to match a PO number). We keep whatever arrived in the DTO and only
        //      apply the auto-fill below when it is still empty.
        //   2) VariableSymbolIsManualOverride = false (default, covers all UI flows) →
        //      always re-derive from DocumentNumber. This is the key guard for issue #107:
        //      the UI preview sends VS = digits(preview-without-prefix), but after
        //      GenerateDocumentNumberAsync applies the client prefix the DocumentNumber is
        //      e.g. "EU-2026001". Re-deriving here gives VS = "2026001" (correct digits from
        //      the prefixed number) instead of persisting the stale preview value.
        if (!createDto.VariableSymbolIsManualOverride && !string.IsNullOrEmpty(invoice.DocumentNumber))
        {
            // Always overwrite — discard any UI-supplied preview VS so it matches the real DocumentNumber.
            invoice.VariableSymbol = new string(invoice.DocumentNumber
                .Where(char.IsDigit).Take(10).ToArray());
        }
        else if (string.IsNullOrEmpty(invoice.VariableSymbol) && !string.IsNullOrEmpty(invoice.DocumentNumber))
        {
            // Manual-override path: VS was empty anyway → derive from DocumentNumber as fallback.
            invoice.VariableSymbol = new string(invoice.DocumentNumber
                .Where(char.IsDigit).Take(10).ToArray());
        }

        // Check for duplicate Variable Symbol (VS) before saving.
        // Czech banking requires unique VS per invoice — duplicate VS would cause
        // payment matching issues (bank can't tell which invoice was paid).
        if (!string.IsNullOrEmpty(invoice.VariableSymbol))
        {
            var duplicateExists = await _context.Invoice
                .AsNoTracking()
                .AnyAsync(i => i.Id != invoice.Id
                    && i.VariableSymbol == invoice.VariableSymbol
                    && i.Status != EInvoiceStatus.Deleted,
                    cancellationToken);

            if (duplicateExists)
            {
                _logger.LogWarning(
                    "Duplicate VariableSymbol '{VS}' detected for invoice ID {Id}",
                    invoice.VariableSymbol, invoice.Id);
                throw new InvalidOperationException(
                    $"An invoice with Variable Symbol '{invoice.VariableSymbol}' already exists. " +
                    "Each invoice must have a unique Variable Symbol for payment tracking.");
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created {DocumentType} with ID {Id}, DocumentNumber {DocumentNumber}",
            invoice.DocumentType, invoice.Id, invoice.DocumentNumber);

        // Reload with related entities
        return (await GetInvoiceByIdAsync(invoice.Id, cancellationToken))!;
    }

    /// <summary>
    /// Resolves the invoice's BankAccountNumber/IBAN/SWIFT from the issuer's stored bank
    /// accounts. This is the single place all invoice creation/update paths route through for
    /// bank account resolution — fixes the gap where MCP-created invoices had no bank account
    /// at all (not even pre-filled), because MCP callers never set the string fields directly.
    ///
    /// Two independent jobs, run in order:
    ///   1. <paramref name="bankAccountId"/> given → load that <see cref="BankAccount"/>
    ///      (must belong to <paramref name="issuerId"/>, else 400 via InvalidOperationException)
    ///      and copy its three string fields onto the invoice, overriding whatever explicit
    ///      strings the caller also sent. This always runs, regardless of
    ///      <paramref name="applyDefaulting"/> — an explicit ID is never a "guess".
    ///   2. Otherwise, when <paramref name="applyDefaulting"/> is true AND the invoice has no
    ///      bank fields at all AND the payment method is bank transfer (or unset — most callers,
    ///      including every MCP tool, never set a payment method), pick one of the issuer's
    ///      accounts: IsDefault matching the invoice currency → any account matching the
    ///      currency → IsDefault (any currency) → first account by Id.
    ///      If the issuer has no accounts yet, fields are left null — nothing to fill from.
    ///
    /// <paramref name="applyDefaulting"/> is false for the proforma→final and copy-invoice paths:
    /// those already copy the source document's bank fields verbatim (even when empty) and must
    /// not have step 2 silently invent an account the source never had.
    /// </summary>
    private async Task ApplyBankAccountDefaultsAsync(
        Invoice invoice, long issuerId, long currencyId, long? bankAccountId,
        bool applyDefaulting, CancellationToken cancellationToken)
    {
        if (bankAccountId.HasValue)
        {
            var account = await _context.BankAccount
                .FirstOrDefaultAsync(a => a.Id == bankAccountId.Value && a.ClientId == issuerId, cancellationToken);

            if (account == null)
                throw new InvalidOperationException(
                    $"Bank account with ID {bankAccountId} not found for this company.");

            invoice.BankAccountNumber = account.AccountNumber;
            invoice.IBAN = account.IBAN;
            invoice.SWIFT = account.SWIFT;
            return;
        }

        if (!applyDefaulting)
            return;

        var hasExplicitBankData = !string.IsNullOrWhiteSpace(invoice.BankAccountNumber)
            || !string.IsNullOrWhiteSpace(invoice.IBAN)
            || !string.IsNullOrWhiteSpace(invoice.SWIFT);
        if (hasExplicitBankData)
            return;

        // Only auto-fill for bank transfer. Treat "no payment method at all" as bank transfer too
        // (the common case — most callers, especially MCP/chat, never set PaymentMethod).
        if (invoice.PaymentMethod.HasValue && invoice.PaymentMethod != EPaymentMethod.BankTransfer)
            return;

        var issuerAccounts = await _context.BankAccount
            .Where(a => a.ClientId == issuerId)
            .OrderBy(a => a.Id) // deterministic rung choice
            .ToListAsync(cancellationToken);

        if (issuerAccounts.Count == 0)
            return; // Nothing to fill from — issuer hasn't configured a bank account yet.

        var currencyCode = await _context.Currency
            .Where(c => c.Id == currencyId)
            .Select(c => c.Code)
            .FirstOrDefaultAsync(cancellationToken);

        var chosen =
            issuerAccounts.FirstOrDefault(a => a.IsDefault && CurrencyMatches(a, currencyCode))
            ?? issuerAccounts.FirstOrDefault(a => CurrencyMatches(a, currencyCode))
            ?? issuerAccounts.FirstOrDefault(a => a.IsDefault)
            ?? issuerAccounts.First();

        invoice.BankAccountNumber = chosen.AccountNumber;
        invoice.IBAN = chosen.IBAN;
        invoice.SWIFT = chosen.SWIFT;

        static bool CurrencyMatches(BankAccount account, string? currencyCode) =>
            !string.IsNullOrEmpty(currencyCode)
            && string.Equals(account.CurrencyCode, currencyCode, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<InvoiceDto?> UpdateInvoiceAsync(long invoiceId, UpdateInvoiceDto updateDto, CancellationToken cancellationToken = default)
    {
        var invoice = await _context.Invoice
            .Include(i => i.InvoiceItem)
            .FirstOrDefaultAsync(i => i.Id == invoiceId && i.Status != EInvoiceStatus.Deleted, cancellationToken);

        if (invoice == null)
            return null;

        // Validate status - only Draft and Completed can be updated
        if (invoice.Status == EInvoiceStatus.Paid)
            throw new InvalidOperationException("Cannot update paid invoices");

        if (invoice.Status == EInvoiceStatus.Creditnoted)
            throw new InvalidOperationException("Cannot update creditnoted invoices");

        _logger.LogInformation("Updating {DocumentType} {Id}", invoice.DocumentType, invoice.Id);

        // Update fields
        // NormalizeToUtcMidnight: date-only fields must be stored as UTC midnight to prevent
        // timezone-induced day shifts (see CreateInvoiceAsync for detailed explanation).
        if (updateDto.IssueDate.HasValue)
            invoice.IssueDate = NormalizeToUtcMidnight(updateDto.IssueDate)!.Value;

        if (updateDto.DueDate.HasValue)
            invoice.DueDate = NormalizeToUtcMidnight(updateDto.DueDate)!.Value;

        if (updateDto.TaxableSupplyDate.HasValue)
            invoice.TaxableSupplyDate = NormalizeToUtcMidnight(updateDto.TaxableSupplyDate)!.Value;

        if (updateDto.VariableSymbol != null)
            invoice.VariableSymbol = updateDto.VariableSymbol;

        if (updateDto.ConstantSymbol != null)
            invoice.ConstantSymbol = updateDto.ConstantSymbol;

        if (updateDto.SpecificSymbol != null)
            invoice.SpecificSymbol = updateDto.SpecificSymbol;

        if (updateDto.BankAccountNumber != null)
            invoice.BankAccountNumber = updateDto.BankAccountNumber;

        if (updateDto.IBAN != null)
            invoice.IBAN = updateDto.IBAN;

        if (updateDto.SWIFT != null)
            invoice.SWIFT = updateDto.SWIFT;

        // BankAccountId wins over any explicit strings above — same rule as CreateInvoiceAsync.
        // applyDefaulting: false — an update never auto-invents a bank account that wasn't asked for.
        if (updateDto.BankAccountId.HasValue)
            await ApplyBankAccountDefaultsAsync(
                invoice, invoice.IssuerId, updateDto.CurrencyId ?? invoice.CurrencyId, updateDto.BankAccountId,
                applyDefaulting: false, cancellationToken);

        if (updateDto.PaymentMethod != null)
            invoice.PaymentMethod = updateDto.PaymentMethod;

        if (updateDto.CurrencyId.HasValue)
            invoice.CurrencyId = updateDto.CurrencyId.Value;

        if (updateDto.Notes != null)
            invoice.Notes = updateDto.Notes;

        // Update items if provided
        if (updateDto.InvoiceItem != null)
        {
            // Get issuer to validate VAT requirements
            var issuer = await _context.Client.FindAsync(new object[] { invoice.IssuerId }, cancellationToken);
            if (issuer == null)
                throw new InvalidOperationException($"Issuer with ID {invoice.IssuerId} not found");

            // Validate VAT requirements: If issuer is VAT payer, all billable items must have VatRateId
            // Text rows are excluded — they have no financial data.
            if (issuer.IsVatPayer)
            {
                var itemsWithoutVatRate = updateDto.InvoiceItem
                    .Where(i => !i.IsTextRow && !i.VatRateId.HasValue).ToList();
                if (itemsWithoutVatRate.Any())
                {
                    throw new InvalidOperationException(
                        "When issuer is a VAT payer, all invoice items must have a VAT rate assigned (VatRateId). " +
                        $"Found {itemsWithoutVatRate.Count} item(s) without VAT rate.");
                }
            }
            else
            {
                // Non-VAT payer: never charge VAT, whatever the caller sent (see NonVatPayerItems).
                NonVatPayerItems.StripVat(updateDto.InvoiceItem);
            }

            // Validate Reverse Charge rules (same as in CreateInvoiceAsync).
            ValidateReverseChargeCodes(updateDto.InvoiceItem);

            // Remove old items
            _context.InvoiceItem.RemoveRange(invoice.InvoiceItem);

            // Add new items
            decimal totalBeforeVat = 0;
            decimal totalVat = 0;

            foreach (var itemDto in updateDto.InvoiceItem)
            {
                var item = new InvoiceItem
                {
                    OrderIndex = itemDto.OrderIndex,
                    IsTextRow = itemDto.IsTextRow,
                    Description = itemDto.Description,
                    ProductCode = itemDto.ProductCode,
                    Notes = itemDto.Notes
                };

                if (itemDto.IsTextRow)
                {
                    item.Quantity = 0;
                    item.UnitPrice = 0;
                    item.Unit = "";
                    invoice.InvoiceItem.Add(item);
                    continue;
                }

                decimal vatRatePercentage = itemDto.VatRatePercentage;
                if (itemDto.VatRateId.HasValue)
                {
                    var vatRate = await _context.VatRate.FindAsync(new object[] { itemDto.VatRateId.Value }, cancellationToken);
                    if (vatRate == null)
                        throw new InvalidOperationException($"VAT rate with ID {itemDto.VatRateId} not found");
                    vatRatePercentage = vatRate.Rate;
                }

                item.Quantity = itemDto.Quantity;
                item.Unit = itemDto.Unit;
                item.UnitPrice = itemDto.UnitPrice;
                item.VatRateId = itemDto.VatRateId;
                item.VatRatePercentage = vatRatePercentage;
                item.VatRegime = itemDto.VatRegime;
                item.ReverseChargeCodeId = itemDto.ReverseChargeCodeId;

                item.TotalBeforeVat = item.Quantity * item.UnitPrice;
                // Round VatAmount consistently (same rule as CreateInvoiceAsync —
                // AwayFromZero matches standard Czech VAT rounding and eliminates
                // drift when deduction rows are back-calculated from TotalWithVat).
                CalculateItemVat(item);

                invoice.InvoiceItem.Add(item);

                totalBeforeVat += item.TotalBeforeVat;
                // Only Standard regime items contribute to billed VAT (same rule as Create).
                totalVat += item.VatAmount;
            }

            invoice.TotalBeforeVat = totalBeforeVat;
            invoice.TotalVat = totalVat;
            invoice.TotalWithVat = totalBeforeVat + totalVat;
        }

        await _context.SaveChangesAsync(cancellationToken);

        return await GetInvoiceByIdAsync(invoice.Id, cancellationToken);
    }

    public async Task<InvoiceDto?> CompleteInvoiceAsync(long invoiceId, CancellationToken cancellationToken = default)
    {
        var invoice = await _context.Invoice
            .Include(i => i.Issuer)
                .ThenInclude(issuer => issuer.BillingSettings)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);

        if (invoice == null)
            return null;

        if (invoice.Status != EInvoiceStatus.Draft)
            throw new InvalidOperationException($"Invoice is already {invoice.Status}");

        // Readiness gate (#206): issuing a document is the point of no return — the number is
        // drawn from the sequence and the document becomes a tax record. Refuse it while the
        // mandatory company settings are incomplete (address, IČO/DIČ, bank account, number
        // sequence) instead of producing a document the customer cannot pay.
        //
        // Scoped deliberately: only the issuer of THIS invoice and only the document type
        // being issued, so an unrelated gap elsewhere in the tenant does not block the user.
        // Warnings (EPO header) never throw. Runs before any state change — see the tests.
        await _tenantReadinessService.EnsureReadyAsync(
            invoice.IssuerId, invoice.DocumentType, cancellationToken);

        _logger.LogInformation("Completing {DocumentType} {Id}", invoice.DocumentType, invoice.Id);

        // Document number should already be generated at creation time.
        // This fallback handles legacy invoices that may still have "DRAFT".
        if (invoice.DocumentNumber == "DRAFT")
        {
            invoice.DocumentNumber = await GenerateDocumentNumberAsync(invoice, cancellationToken);
        }

        // Ensure VariableSymbol is set — digits only, max 10 (Czech banking requirement)
        if (string.IsNullOrEmpty(invoice.VariableSymbol) && !string.IsNullOrEmpty(invoice.DocumentNumber))
        {
            invoice.VariableSymbol = new string(invoice.DocumentNumber
                .Where(char.IsDigit).Take(10).ToArray());
        }

        // Check for duplicate Variable Symbol before completing.
        // Same guard as in CreateInvoiceAsync — prevents duplicate VS on legacy DRAFT invoices.
        if (!string.IsNullOrEmpty(invoice.VariableSymbol))
        {
            var duplicateExists = await _context.Invoice
                .AsNoTracking()
                .AnyAsync(i => i.Id != invoice.Id
                    && i.VariableSymbol == invoice.VariableSymbol
                    && i.Status != EInvoiceStatus.Deleted,
                    cancellationToken);

            if (duplicateExists)
            {
                throw new InvalidOperationException(
                    $"An invoice with Variable Symbol '{invoice.VariableSymbol}' already exists. " +
                    "Each invoice must have a unique Variable Symbol for payment tracking.");
            }
        }

        invoice.Status = EInvoiceStatus.Completed;

        await _context.SaveChangesAsync(cancellationToken);

        return await GetInvoiceByIdAsync(invoice.Id, cancellationToken);
    }

    public async Task<InvoiceDto?> MarkAsPaidAsync(long invoiceId, DateTime? paidAt = null, CancellationToken cancellationToken = default)
    {
        var invoice = await _context.Invoice.FindAsync(new object[] { invoiceId }, cancellationToken);

        if (invoice == null)
            return null;

        if (invoice.Status != EInvoiceStatus.Completed)
            throw new InvalidOperationException("Only completed invoices can be marked as paid");

        _logger.LogInformation("Marking {DocumentType} {Id} as paid", invoice.DocumentType, invoice.Id);

        invoice.Status = EInvoiceStatus.Paid;
        invoice.PaidAt = paidAt ?? DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        return await GetInvoiceByIdAsync(invoice.Id, cancellationToken);
    }

    public async Task<bool> DeleteInvoiceAsync(long invoiceId, CancellationToken cancellationToken = default)
    {
        var invoice = await _context.Invoice.FindAsync(new object[] { invoiceId }, cancellationToken);

        if (invoice == null)
            return false;

        // Paid or Creditnoted invoices cannot be deleted under any circumstances —
        // they have financial implications (payments received, credit notes issued).
        if (invoice.Status == EInvoiceStatus.Paid || invoice.Status == EInvoiceStatus.Creditnoted)
            throw new InvalidOperationException(
                $"Cannot delete a {invoice.Status} invoice. Only Draft or the last Completed invoice can be deleted.");

        if (invoice.Status == EInvoiceStatus.Deleted)
            throw new InvalidOperationException("Invoice is already deleted.");

        // Completed invoices can only be deleted if they are the LAST issued document
        // of their type (Invoice or CreditNote). Deleting a Completed invoice in the middle
        // of the sequence would break the continuous numbering required by tax law.
        if (invoice.Status == EInvoiceStatus.Completed)
        {
            var isLast = await IsLastIssuedInvoiceAsync(invoice, cancellationToken);
            if (!isLast)
                throw new InvalidOperationException(
                    "Only the last completed invoice can be deleted. " +
                    "This invoice has subsequent documents in the numbering sequence.");
        }

        _logger.LogInformation("Deleting {DocumentType} {Id} (Status={Status}, DocumentNumber={DocNum})",
            invoice.DocumentType, invoice.Id, invoice.Status, invoice.DocumentNumber);

        // Try to release the document number back to the sequence so it can be reused.
        // This only works if the deleted invoice had the LAST number in the sequence.
        // If another invoice was generated after this one, the number stays consumed
        // to avoid gaps in the middle of the sequence.
        await TryReleaseDocumentNumberAsync(invoice, cancellationToken);

        // Soft delete: mark as Deleted and clear DocumentNumber.
        // The unique index IX_Invoice_DocumentNumber excludes Status=5 (Deleted),
        // but clearing the number explicitly prevents any edge cases and makes it
        // obvious in the DB that the number is no longer in use.
        invoice.Status = EInvoiceStatus.Deleted;
        invoice.DocumentNumber = null;
        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// Checks whether the given invoice is the last issued (non-deleted) document
    /// of its DocumentType. Used to determine if a Completed invoice can be deleted.
    ///
    /// "Last" is determined by the highest ID among non-deleted invoices of the same type,
    /// because IDs are sequential and always increment. This is simpler and more reliable
    /// than parsing document numbers (which may have variable formats with prefixes/suffixes).
    /// </summary>
    private async Task<bool> IsLastIssuedInvoiceAsync(Invoice invoice, CancellationToken ct)
    {
        var lastId = await _context.Invoice
            .Where(i => i.DocumentType == invoice.DocumentType
                     && i.Status != EInvoiceStatus.Deleted)
            .MaxAsync(i => (long?)i.Id, ct);

        return lastId == invoice.Id;
    }

    /// <summary>
    /// Restores a soft-deleted invoice back to Draft status.
    /// Only invoices with Status == Deleted can be restored — all other statuses
    /// throw InvalidOperationException so the caller can return 400 Bad Request.
    /// After restoring, the invoice is re-fetched with all navigation properties
    /// to return a fully populated DTO.
    /// </summary>
    /// <param name="invoiceId">ID of the invoice to restore</param>
    /// <param name="cancellationToken">Cancellation token for async operations</param>
    /// <returns>Restored invoice DTO, or null if the invoice does not exist</returns>
    public async Task<InvoiceDto?> RestoreInvoiceAsync(long invoiceId, CancellationToken cancellationToken = default)
    {
        // FindAsync uses the primary key — no need for a LINQ query.
        var invoice = await _context.Invoice.FindAsync(new object[] { invoiceId }, cancellationToken);

        if (invoice == null)
            return null;

        // Only deleted invoices can be restored.
        if (invoice.Status != EInvoiceStatus.Deleted)
            throw new InvalidOperationException("Only deleted invoices can be restored.");

        _logger.LogInformation("Restoring {DocumentType} {Id} to Draft", invoice.DocumentType, invoice.Id);

        // Reset status back to Draft so the user can edit and re-issue the invoice.
        invoice.Status = EInvoiceStatus.Draft;

        await _context.SaveChangesAsync(cancellationToken);

        // Re-fetch with all navigation properties to return a complete DTO.
        return await GetInvoiceByIdAsync(invoice.Id, cancellationToken);
    }

    /// <summary>
    /// Reverts a Completed invoice back to Draft status so it can be fully edited.
    /// Only Completed invoices can be reverted — Paid and Creditnoted cannot.
    /// The document number is preserved so the user can re-issue with the same number.
    /// </summary>
    public async Task<InvoiceDto?> RevertToDraftAsync(long invoiceId, CancellationToken cancellationToken = default)
    {
        var invoice = await _context.Invoice.FindAsync(new object[] { invoiceId }, cancellationToken);

        if (invoice == null)
            return null;

        if (invoice.Status != EInvoiceStatus.Completed)
            throw new InvalidOperationException(
                $"Only completed invoices can be reverted to draft. Current status: {invoice.Status}");

        _logger.LogInformation("Reverting {DocumentType} {Id} from Completed to Draft", invoice.DocumentType, invoice.Id);

        invoice.Status = EInvoiceStatus.Draft;
        await _context.SaveChangesAsync(cancellationToken);

        return await GetInvoiceByIdAsync(invoice.Id, cancellationToken);
    }

    public async Task<InvoiceDto> CreateCreditNoteAsync(long originalInvoiceId, CreateInvoiceDto createDto, CancellationToken cancellationToken = default)
    {
        var originalInvoice = await _context.Invoice
            .Include(i => i.Client)
            .Include(i => i.Issuer)
            .FirstOrDefaultAsync(i => i.Id == originalInvoiceId, cancellationToken);

        if (originalInvoice == null)
            throw new InvalidOperationException($"Original invoice with ID {originalInvoiceId} not found");

        if (originalInvoice.DocumentType != EDocumentType.Invoice)
            throw new InvalidOperationException("Credit notes can only be created for invoices");

        _logger.LogInformation("Creating credit note for invoice {InvoiceId}", originalInvoiceId);

        // Set required fields for credit note
        createDto.DocumentType = EDocumentType.CreditNote;
        createDto.OriginalInvoiceId = originalInvoiceId;
        createDto.ClientId = originalInvoice.ClientId ?? throw new InvalidOperationException("Original invoice has no ClientId");
        createDto.IssuerId = originalInvoice.IssuerId;

        var creditNote = await CreateInvoiceAsync(createDto, cancellationToken);

        // Mark original invoice as creditnoted
        originalInvoice.Status = EInvoiceStatus.Creditnoted;
        await _context.SaveChangesAsync(cancellationToken);

        return creditNote;
    }

    public async Task<List<InvoiceDto>> GetCreditNotesForInvoiceAsync(long invoiceId, CancellationToken cancellationToken = default)
    {
        // AsNoTracking: read-only list query — results are mapped to DTOs
        var creditNotes = await _context.Invoice
            .AsNoTracking()
            .Include(i => i.Client)
            .Include(i => i.Issuer)
            .Include(i => i.Currency)
            .Include(i => i.InvoiceItem.OrderBy(item => item.OrderIndex))
                .ThenInclude(item => item.ReverseChargeCode)
            .Include(i => i.OriginalInvoice)
            .Where(i => i.OriginalInvoiceId == invoiceId && i.DocumentType == EDocumentType.CreditNote && i.Status != EInvoiceStatus.Deleted)
            .OrderByDescending(i => i.IssueDate ?? DateTime.MinValue)
            .ToListAsync(cancellationToken);

        return creditNotes.Select(i => MapToDto(i)).ToList();
    }

    // ─── Proforma cross-link queries ──────────────────────────────────────────

    /// <inheritdoc />
    public async Task<List<InvoiceDto>> GetFinalInvoicesForProformaAsync(
        long proformaId, CancellationToken cancellationToken = default)
    {
        // Return all standard Invoices whose OriginalInvoiceId points to this proforma.
        // These are the final invoices issued against the advance payment.
        var items = await _context.Invoice
            .AsNoTracking()
            .Include(i => i.Client)
            .Include(i => i.Issuer)
            .Include(i => i.Currency)
            .Include(i => i.InvoiceItem.OrderBy(item => item.OrderIndex))
                .ThenInclude(item => item.ReverseChargeCode)
            .Include(i => i.OriginalInvoice)
            .Where(i => i.OriginalInvoiceId == proformaId
                     && i.DocumentType == EDocumentType.Invoice
                     && i.Status != EInvoiceStatus.Deleted)
            .OrderByDescending(i => i.IssueDate ?? DateTime.MinValue)
            .ToListAsync(cancellationToken);

        return items.Select(MapToDto).ToList();
    }

    /// <inheritdoc />
    public async Task<List<InvoiceDto>> GetTaxReceiptsForProformaAsync(
        long proformaId, CancellationToken cancellationToken = default)
    {
        // Return all TaxReceiptForAdvance documents whose OriginalInvoiceId points to this proforma.
        // These are auto-issued (or manually issued) DPP documents.
        var items = await _context.Invoice
            .AsNoTracking()
            .Include(i => i.Client)
            .Include(i => i.Issuer)
            .Include(i => i.Currency)
            .Include(i => i.InvoiceItem.OrderBy(item => item.OrderIndex))
                .ThenInclude(item => item.ReverseChargeCode)
            .Include(i => i.OriginalInvoice)
            .Where(i => i.OriginalInvoiceId == proformaId
                     && i.DocumentType == EDocumentType.TaxReceiptForAdvance
                     && i.Status != EInvoiceStatus.Deleted)
            .OrderByDescending(i => i.IssueDate ?? DateTime.MinValue)
            .ToListAsync(cancellationToken);

        return items.Select(MapToDto).ToList();
    }

    // ─── Proforma → Final Invoice ─────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<InvoiceDto> IssueFinalInvoiceAsync(
        long proformaId,
        IssueFinalInvoiceDto dto,
        CancellationToken cancellationToken = default)
    {
        // Load the proforma with all related data we need for validation and VAT breakdown.
        var proforma = await _context.Invoice
            .Include(i => i.InvoiceItem)
            .Include(i => i.Client)
                .ThenInclude(c => c!.BillingSettings)
            .Include(i => i.Issuer)
            .FirstOrDefaultAsync(i => i.Id == proformaId, cancellationToken);

        if (proforma == null)
            throw new KeyNotFoundException($"Proforma with ID {proformaId} not found");

        // Only Proforma documents can be used as the basis for a final invoice.
        if (proforma.DocumentType != EDocumentType.Proforma)
            throw new InvalidOperationException(
                $"Document {proformaId} is of type {proforma.DocumentType}, not Proforma. " +
                "Only Proforma documents can be used to issue a final invoice.");

        // The proforma must have a received payment to deduct.
        if (proforma.PaidAmount <= 0)
            throw new InvalidOperationException(
                $"Proforma {proformaId} has no received payment (PaidAmount = {proforma.PaidAmount:F2}). " +
                "Mark the proforma as paid (or let payment matching do it) before issuing the final invoice.");

        // Calculate how much of the advance is still available to deduct.
        var alreadyDeducted = await GetAlreadyDeductedAmountAsync(proformaId, cancellationToken);
        var remainingAdvance = proforma.PaidAmount - alreadyDeducted;

        if (remainingAdvance < 0)
            remainingAdvance = 0; // safety clamp — should not happen in practice

        // Resolve the requested deduction amount.
        // When the caller does not specify one, deduct the full remaining advance.
        var requestedDeduction = dto.DeductionAmount ?? remainingAdvance;

        // Validate that the requested deduction does not exceed the remaining advance.
        if (requestedDeduction <= 0)
            throw new InvalidOperationException(
                $"No advance balance remaining on proforma {proformaId}. " +
                $"Already deducted: {alreadyDeducted:F2}, paid: {proforma.PaidAmount:F2}.");

        if (requestedDeduction > remainingAdvance)
            throw new InvalidOperationException(
                $"Requested deduction {requestedDeduction:F2} exceeds remaining advance " +
                $"{remainingAdvance:F2} on proforma {proformaId}.");

        _logger.LogInformation(
            "Issuing final invoice from proforma {ProformaId}: deduction {Deduction:F2}, remaining was {Remaining:F2}",
            proformaId, requestedDeduction, remainingAdvance);

        // Build the deduction rows by splitting the deduction amount proportionally
        // across the VAT rates present on the proforma items.
        var deductionItems = BuildDeductionItems(proforma.InvoiceItem, requestedDeduction);

        // Combine the caller-supplied "real" items with the auto-generated deduction rows.
        // Deduction rows are appended after the real items with OrderIndex starting after them.
        var allItems = new List<CreateInvoiceItemDto>(dto.InvoiceItem);
        var nextOrderIndex = allItems.Count > 0
            ? allItems.Max(i => i.OrderIndex) + 1
            : 1;

        foreach (var deductionItem in deductionItems)
        {
            deductionItem.OrderIndex = nextOrderIndex++;
            allItems.Add(deductionItem);
        }

        // Build the CreateInvoiceDto that re-uses the existing creation pipeline.
        // All header fields (client, issuer, currency, bank account…) are copied from the proforma
        // so the final invoice is consistent with it. The caller may override dates and notes.
        var createDto = new CreateInvoiceDto
        {
            DocumentType = EDocumentType.Invoice,
            ClientId = proforma.ClientId ?? throw new InvalidOperationException("Proforma has no ClientId"),
            IssuerId = proforma.IssuerId,
            IssueDate = dto.IssueDate,
            DueDate = dto.DueDate,
            TaxableSupplyDate = dto.TaxableSupplyDate,
            OriginalInvoiceId = proformaId,           // 1:N link — final invoice → proforma
            VariableSymbol = null,                    // auto-generated from document number
            ConstantSymbol = proforma.ConstantSymbol,
            SpecificSymbol = proforma.SpecificSymbol,
            BankAccountNumber = proforma.BankAccountNumber,
            IBAN = proforma.IBAN,
            SWIFT = proforma.SWIFT,
            PaymentMethod = proforma.PaymentMethod,
            CurrencyId = proforma.CurrencyId,
            Notes = dto.Notes,
            NumberSequenceId = dto.NumberSequenceId,
            InvoiceItem = allItems
        };

        // Re-use the standard invoice creation pipeline (document number, VS, totals, …).
        // applyBankAccountDefaulting: false — the bank fields above were just copied verbatim
        // from the proforma (even when empty); the resolver must not override that.
        var finalInvoice = await CreateInvoiceCoreAsync(createDto, applyBankAccountDefaulting: false, cancellationToken);

        _logger.LogInformation(
            "Final invoice {FinalId} ({DocNum}) issued from proforma {ProformaId}. " +
            "Deduction: {Deduction:F2}, Total with VAT: {Total:F2}",
            finalInvoice.Id, finalInvoice.DocumentNumber,
            proformaId, requestedDeduction, finalInvoice.TotalWithVat);

        return finalInvoice;
    }

    /// <inheritdoc />
    public async Task<decimal> GetRemainingAdvanceAsync(long proformaId, CancellationToken cancellationToken = default)
    {
        var proforma = await _context.Invoice
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == proformaId && i.DocumentType == EDocumentType.Proforma, cancellationToken);

        if (proforma == null)
            return 0;

        var alreadyDeducted = await GetAlreadyDeductedAmountAsync(proformaId, cancellationToken);
        var remaining = proforma.PaidAmount - alreadyDeducted;
        return remaining < 0 ? 0 : remaining;
    }

    // ─── Deduction Helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Calculates the sum of advance deductions already issued against the given proforma.
    ///
    /// A deduction row is identified by:
    ///  - Being on a final Invoice linked to this proforma (OriginalInvoiceId = proformaId, DocumentType = Invoice)
    ///  - Having a negative TotalBeforeVat (the deduction is always negative)
    ///  - Not being on a Deleted invoice
    ///
    /// We sum the negative TotalWithVat values (which include VAT) to get the total amount
    /// already deducted so far, then negate (make positive) for comparison with PaidAmount.
    /// </summary>
    private async Task<decimal> GetAlreadyDeductedAmountAsync(long proformaId, CancellationToken ct)
    {
        // Sum the TotalWithVat of all negative (deduction) items on linked final invoices.
        // TotalWithVat is negative for deduction rows, so summing gives a negative number.
        // We negate it to get the positive "amount already deducted".
        var negativeSum = await _context.InvoiceItem
            .AsNoTracking()
            .Where(item =>
                item.Invoice != null
                && item.Invoice.OriginalInvoiceId == proformaId
                && item.Invoice.DocumentType == EDocumentType.Invoice
                && item.Invoice.Status != EInvoiceStatus.Deleted
                && item.TotalBeforeVat < 0)
            .SumAsync(item => (decimal?)item.TotalWithVat, ct) ?? 0m;

        // negativeSum is ≤ 0; negate to get the positive deducted amount.
        return -negativeSum;
    }

    /// <summary>
    /// Splits <paramref name="totalDeductionWithVat"/> into one deduction row per VAT rate
    /// found on the proforma items, proportional to each rate's share of the total proforma
    /// amount (TotalWithVat-based weights).
    ///
    /// WHY this approach:
    /// Czech VAT law (§ 28/5 ZDPH) requires that the deduction row carries the same VAT rate
    /// as the original advance. Splitting proportionally by TotalWithVat weight ensures that
    /// each rate's share of the advance is correctly deducted, keeping total Output VAT neutral.
    ///
    /// Rounding strategy — "largest remainder":
    /// After distributing amounts (rounded to 2 decimal places), any rounding residual
    /// (caused by integer division of the total) is assigned to the row with the largest
    /// fractional part. This keeps the sum of deduction rows exactly equal to the requested
    /// deduction, with at most 1-cent deviation per row.
    /// </summary>
    private static List<CreateInvoiceItemDto> BuildDeductionItems(
        ICollection<InvoiceItem> proformaItems,
        decimal totalDeductionWithVat)
    {
        // Collect the non-text, non-zero items from the proforma, grouped by VAT rate.
        // We use TotalWithVat (the actual amount the client paid) as the weight basis
        // because the paid amount (PaidAmount) is also TotalWithVat-based.
        var vatGroups = proformaItems
            .Where(i => !i.IsTextRow && i.TotalWithVat != 0)
            .GroupBy(i => i.VatRatePercentage)
            .Select(g => new
            {
                VatRatePercentage = g.Key,
                VatRateId = g.First().VatRateId,
                TotalWithVat = g.Sum(i => i.TotalWithVat)
            })
            .ToList();

        // If there are no billable proforma items at all, fall back to a single 0% row
        // so we still produce a deduction row (better than silently omitting it).
        if (vatGroups.Count == 0)
        {
            return new List<CreateInvoiceItemDto>
            {
                new()
                {
                    Description = "Odečet přijaté zálohy / Advance payment deduction",
                    Quantity = 1,
                    Unit = "pcs",
                    UnitPrice = -totalDeductionWithVat,
                    VatRatePercentage = 0,
                    VatRateId = null
                }
            };
        }

        // Total proforma TotalWithVat across all billable items — used as the denominator.
        var proformaTotalWithVat = vatGroups.Sum(g => g.TotalWithVat);

        if (proformaTotalWithVat == 0)
        {
            // Proforma has items but they net to zero — edge case, single 0% row.
            return new List<CreateInvoiceItemDto>
            {
                new()
                {
                    Description = "Odečet přijaté zálohy / Advance payment deduction",
                    Quantity = 1,
                    Unit = "pcs",
                    UnitPrice = -totalDeductionWithVat,
                    VatRatePercentage = 0,
                    VatRateId = null
                }
            };
        }

        // ── Proportional split with "largest remainder" rounding ──────────────
        // Step 1: compute the exact (unrounded) share for each VAT group.
        // Step 2: floor to 2 decimal places and collect the fractional remainder.
        // Step 3: distribute rounding cents (if any) to groups with largest remainder.

        var shares = vatGroups.Select(g => new
        {
            g.VatRatePercentage,
            g.VatRateId,
            ExactShare = totalDeductionWithVat * (g.TotalWithVat / proformaTotalWithVat)
        }).ToList();

        var floored = shares.Select(s => Math.Round(s.ExactShare, 2, MidpointRounding.ToZero)).ToList();
        var sumFloored = floored.Sum();
        var residual = Math.Round(totalDeductionWithVat - sumFloored, 2);

        // Each residual cent is +0.01; distribute to the groups with the largest fractional part.
        var remainders = shares
            .Select((s, idx) => (Idx: idx, Frac: s.ExactShare - floored[idx]))
            .OrderByDescending(x => x.Frac)
            .ToList();

        var centsToDistribute = (int)Math.Round(residual / 0.01m);
        for (var i = 0; i < centsToDistribute && i < remainders.Count; i++)
        {
            floored[remainders[i].Idx] += 0.01m;
        }

        // ── Build deduction CreateInvoiceItemDto rows ─────────────────────────
        // Each row uses UnitPrice = -(base before VAT) and VatRatePercentage = rate.
        // The standard item calculation pipeline (Quantity * UnitPrice, + VAT) then
        // produces the correct negative TotalWithVat.
        var result = new List<CreateInvoiceItemDto>();

        for (var i = 0; i < shares.Count; i++)
        {
            var rate = shares[i].VatRatePercentage;
            var deductionWithVat = floored[i];

            if (deductionWithVat == 0)
                continue; // skip zero-amount rows (can happen with rounding on tiny amounts)

            // Back-calculate the base (before VAT) from the TotalWithVat deduction.
            // TotalWithVat = TotalBeforeVat * (1 + rate/100)
            // → TotalBeforeVat = TotalWithVat / (1 + rate/100)
            var divisor = 1m + rate / 100m;
            var deductionBase = Math.Round(deductionWithVat / divisor, 2, MidpointRounding.AwayFromZero);

            result.Add(new CreateInvoiceItemDto
            {
                Description = "Odečet přijaté zálohy / Advance payment deduction",
                Quantity = 1,
                Unit = "pcs",
                UnitPrice = -deductionBase,          // negative — this is a deduction
                VatRatePercentage = rate,
                VatRateId = shares[i].VatRateId
            });
        }

        return result;
    }

    // ─── Bulk Operations ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<BulkOperationResult> BulkCompleteAsync(List<long> invoiceIds, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Bulk completing {Count} invoices", invoiceIds.Count);
        var result = new BulkOperationResult();

        // Process each invoice sequentially — DbContext is NOT thread-safe
        foreach (var id in invoiceIds)
        {
            try
            {
                var completed = await CompleteInvoiceAsync(id, cancellationToken);
                if (completed != null)
                {
                    result.SuccessCount++;
                }
                else
                {
                    result.FailedCount++;
                    result.Errors.Add(new BulkOperationError { InvoiceId = id, Error = "Invoice not found" });
                }
            }
            catch (TenantNotReadyException ex)
            {
                // Caught separately from the generic Exception below so the caller gets the same
                // structured refusal the single-invoice /complete endpoint returns (#342) —
                // otherwise only the flattened ex.Message crossed the bulk boundary. This is the
                // transport half only: today Invoices.razor renders just the counts, not
                // result.Errors, so nothing displays these fields yet (follow-up #390).
                result.FailedCount++;
                result.Errors.Add(new BulkOperationError
                {
                    InvoiceId = id,
                    Error = ex.Message,
                    Code = ex.Code,
                    MissingFields = ex.MissingFields
                });
                _logger.LogWarning("Bulk complete failed for invoice {Id} — tenant not ready: {MissingFields}",
                    id, string.Join(", ", ex.MissingFields));
            }
            catch (Exception ex)
            {
                result.FailedCount++;
                result.Errors.Add(new BulkOperationError { InvoiceId = id, Error = ex.Message });
                _logger.LogWarning("Bulk complete failed for invoice {Id}: {Error}", id, ex.Message);
            }
        }

        _logger.LogInformation("Bulk complete finished: {Success} succeeded, {Failed} failed",
            result.SuccessCount, result.FailedCount);
        return result;
    }

    /// <inheritdoc />
    public async Task<BulkOperationResult> BulkMarkAsPaidAsync(List<long> invoiceIds, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Bulk marking {Count} invoices as paid", invoiceIds.Count);
        var result = new BulkOperationResult();

        // Process each invoice sequentially — DbContext is NOT thread-safe
        foreach (var id in invoiceIds)
        {
            try
            {
                var paid = await MarkAsPaidAsync(id, null, cancellationToken);
                if (paid != null)
                {
                    result.SuccessCount++;
                }
                else
                {
                    result.FailedCount++;
                    result.Errors.Add(new BulkOperationError { InvoiceId = id, Error = "Invoice not found" });
                }
            }
            catch (Exception ex)
            {
                result.FailedCount++;
                result.Errors.Add(new BulkOperationError { InvoiceId = id, Error = ex.Message });
                _logger.LogWarning("Bulk mark-paid failed for invoice {Id}: {Error}", id, ex.Message);
            }
        }

        _logger.LogInformation("Bulk mark-paid finished: {Success} succeeded, {Failed} failed",
            result.SuccessCount, result.FailedCount);
        return result;
    }

    /// <inheritdoc />
    public async Task<BulkOperationResult> BulkDeleteAsync(List<long> invoiceIds, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Bulk deleting {Count} invoices", invoiceIds.Count);
        var result = new BulkOperationResult();

        // Process each invoice sequentially — DbContext is NOT thread-safe
        foreach (var id in invoiceIds)
        {
            try
            {
                var deleted = await DeleteInvoiceAsync(id, cancellationToken);
                if (deleted)
                {
                    result.SuccessCount++;
                }
                else
                {
                    result.FailedCount++;
                    result.Errors.Add(new BulkOperationError { InvoiceId = id, Error = "Invoice not found" });
                }
            }
            catch (Exception ex)
            {
                result.FailedCount++;
                result.Errors.Add(new BulkOperationError { InvoiceId = id, Error = ex.Message });
                _logger.LogWarning("Bulk delete failed for invoice {Id}: {Error}", id, ex.Message);
            }
        }

        _logger.LogInformation("Bulk delete finished: {Success} succeeded, {Failed} failed",
            result.SuccessCount, result.FailedCount);
        return result;
    }

    // ─── Mark as Unpaid ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<InvoiceDto> MarkAsUnpaidAsync(long invoiceId, CancellationToken cancellationToken = default)
    {
        // Load the invoice with its PaymentMatch rows so we can unlink them.
        // Include BankTransaction via PaymentMatch so we can recalculate the transaction's MatchStatus.
        var invoice = await _context.Invoice
            .Include(i => i.PaymentMatch)
                .ThenInclude(m => m.BankTransaction)
                    .ThenInclude(t => t.PaymentMatch)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);

        if (invoice == null)
            throw new KeyNotFoundException($"Invoice with ID {invoiceId} not found.");

        // Only Paid invoices can be reverted — all other statuses are rejected.
        if (invoice.Status != EInvoiceStatus.Paid)
            throw new InvalidOperationException(
                $"Only paid invoices can be marked as unpaid. Current status: {invoice.Status}");

        _logger.LogInformation(
            "Marking {DocumentType} {Id} as unpaid (reverting from Paid to Completed)",
            invoice.DocumentType, invoice.Id);

        // Collect affected bank transactions BEFORE removing the match rows,
        // so we can still iterate over them and recalculate their MatchStatus.
        var affectedTransactions = invoice.PaymentMatch
            .Select(m => m.BankTransaction)
            .DistinctBy(t => t.Id)
            .ToList();

        // Remove all PaymentMatch rows that link this invoice to bank transactions.
        // This mirrors the behaviour of PaymentMatchingService.UnmatchAsync but in bulk —
        // every payment record tied to this invoice is deleted so the transaction is free
        // to be matched to a different invoice in the future.
        _context.PaymentMatch.RemoveRange(invoice.PaymentMatch);

        // Recalculate each affected bank transaction's MatchStatus.
        // After removing this invoice's matches, sum what is still assigned on the transaction
        // (other invoice / received-invoice matches from the same transaction, if any).
        foreach (var tx in affectedTransactions)
        {
            var stillAssigned = tx.PaymentMatch
                .Where(m => m.InvoiceId != invoiceId) // this invoice's rows are being removed
                .Sum(m => m.MatchedAmount);

            // Mirror PaymentMatchingService.RecalculateTransactionStatus logic:
            // Unmatched → Matched → PartiallyMatched based on how much is still assigned.
            if (stillAssigned <= 0)
                tx.MatchStatus = EMatchStatus.Unmatched;
            else if (stillAssigned >= tx.Amount)
                tx.MatchStatus = EMatchStatus.Matched;
            else
                tx.MatchStatus = EMatchStatus.PartiallyMatched;
        }

        // Reset the invoice back to Completed (the state before payment was applied).
        invoice.Status = EInvoiceStatus.Completed;
        invoice.PaidAt = null;
        invoice.PaidAmount = 0;

        await _context.SaveChangesAsync(cancellationToken);

        // Re-fetch with all navigation properties to return a complete DTO.
        return (await GetInvoiceByIdAsync(invoice.Id, cancellationToken))!;
    }

    // ─── Copy ─────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<InvoiceDto> CopyInvoiceAsync(long sourceId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Copying invoice {SourceId}", sourceId);

        // Load source invoice with all related data needed for the deep copy.
        // AsNoTracking: we're building a new entity from scratch, so we don't want
        // EF to track the source and accidentally propagate changes to it.
        var source = await _context.Invoice
            .AsNoTracking()
            .Include(i => i.InvoiceItem.OrderBy(item => item.OrderIndex))
            .Include(i => i.Client)
                .ThenInclude(c => c!.BillingSettings)
            .FirstOrDefaultAsync(i => i.Id == sourceId, cancellationToken);

        if (source == null)
            throw new KeyNotFoundException($"Invoice with ID {sourceId} not found");

        // CreditNote sources are not allowed — the Copy icon is hidden in the UI for
        // CreditNotes, and this guard ensures the API enforces the same rule.
        if (source.DocumentType == EDocumentType.CreditNote)
            throw new InvalidOperationException(
                "Copying a CreditNote is not allowed. " +
                "Create a new credit note via the credit note workflow instead.");

        // Build a CreateInvoiceDto from the source so we can re-use the full
        // CreateInvoiceAsync pipeline (document number generation, VS derivation,
        // duplicate VS check, VAT validation, totals calculation, etc.).
        //
        // Key differences from source:
        //   - IssueDate = today (fresh document)
        //   - DueDate = null → CalculateDueDate will re-derive from BillingSettings
        //   - VariableSymbol = null, VariableSymbolIsManualOverride = false → backend derives from new DocumentNumber
        //   - OriginalInvoiceId = null (copy is a standalone document, NOT a credit note)
        //   - NumberSequenceId = null → use default pipeline for the document type
        var createDto = new CreateInvoiceDto
        {
            DocumentType = source.DocumentType,
            ClientId = source.ClientId ?? throw new InvalidOperationException(
                $"Source invoice {sourceId} has no ClientId — cannot copy."),
            IssuerId = source.IssuerId,
            // IssueDate = today so the copy appears as a fresh document.
            // DueDate left null so CalculateDueDate re-derives it from client BillingSettings.
            IssueDate = DateTime.UtcNow,
            DueDate = null,
            // TaxableSupplyDate left null so it defaults to the new IssueDate (Czech accounting: DUZP = IssueDate).
            TaxableSupplyDate = null,
            // Not a credit note — no parent link.
            OriginalInvoiceId = null,
            // VS must NOT be copied from the source — it would duplicate the VS used by the original.
            // Leave null and VariableSymbolIsManualOverride = false so InvoiceService re-derives it
            // from the freshly generated DocumentNumber (same guard as issue #107 fix).
            VariableSymbol = null,
            VariableSymbolIsManualOverride = false,
            ConstantSymbol = source.ConstantSymbol,
            SpecificSymbol = source.SpecificSymbol,
            BankAccountNumber = source.BankAccountNumber,
            IBAN = source.IBAN,
            SWIFT = source.SWIFT,
            PaymentMethod = source.PaymentMethod,
            CurrencyId = source.CurrencyId,
            Notes = source.Notes,
            // NumberSequenceId = null → inherit default sequence for the document type.
            // The source may have been generated from a custom sequence, but the copy
            // should use the standard pipeline unless the user explicitly changes it later.
            NumberSequenceId = null,
            // Deep-copy items: create new InvoiceItemDto instances with no Id/InvoiceId so
            // EF Core treats them as inserts and never touches the source items.
            InvoiceItem = source.InvoiceItem
                .Select(item => new CreateInvoiceItemDto
                {
                    OrderIndex = item.OrderIndex,
                    IsTextRow = item.IsTextRow,
                    Description = item.Description,
                    Quantity = item.Quantity,
                    Unit = item.Unit,
                    UnitPrice = item.UnitPrice,
                    VatRateId = item.VatRateId,
                    VatRatePercentage = item.VatRatePercentage,
                    VatRegime = item.VatRegime,
                    ReverseChargeCodeId = item.ReverseChargeCodeId,
                    ProductCode = item.ProductCode,
                    Notes = item.Notes
                })
                .ToList()
        };

        // Delegate to the full CreateInvoiceAsync pipeline — this handles:
        //   - New document number via GenerateDocumentNumberAsync
        //   - VariableSymbol derived from DocumentNumber
        //   - Duplicate VS check
        //   - VAT validation (issuer IsVatPayer guard)
        //   - ReverseCharge code validation
        //   - Item totals calculation
        //   - Status = Draft (always set by CreateInvoiceAsync)
        // applyBankAccountDefaulting: false — bank fields were just copied verbatim from the
        // source invoice above (even when empty); the resolver must not override that.
        var copy = await CreateInvoiceCoreAsync(createDto, applyBankAccountDefaulting: false, cancellationToken);

        _logger.LogInformation(
            "Invoice copied: source #{SourceId} → new #{CopyId} ({DocNum})",
            sourceId, copy.Id, copy.DocumentNumber);

        return copy;
    }

    // ─── Private Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Validates Reverse Charge code consistency for a list of item DTOs.
    ///
    /// Rules (from AC of issue #45 and §92a ZDPH):
    /// 1. If VatRegime == ReverseCharge, ReverseChargeCodeId MUST be set (not null).
    ///    The code is mandatory for EPO XML (A.1/B.1 sections) and PDF/ISDOC display.
    /// 2. If VatRegime != ReverseCharge, ReverseChargeCodeId MUST be null.
    ///    Mixing regime and code would be confusing and lead to wrong EPO reporting.
    ///
    /// Text rows are excluded — they have no financial data.
    /// </summary>
    private static void ValidateReverseChargeCodes(IEnumerable<CreateInvoiceItemDto> items)
    {
        foreach (var item in items.Where(i => !i.IsTextRow))
        {
            // JSON clients (MCP/API) can send an undefined numeric enum value; reject it up front
            // instead of failing later in CalculateItemVat.
            if (!Enum.IsDefined(item.VatRegime))
                throw new InvalidOperationException(
                    $"Invoice item '{item.Description}' has an invalid VatRegime value '{(int)item.VatRegime}'.");

            if (item.VatRegime == EVatRegime.ReverseCharge && !item.ReverseChargeCodeId.HasValue)
                throw new InvalidOperationException(
                    $"Invoice item '{item.Description}' has VatRegime=ReverseCharge but no ReverseChargeCodeId. " +
                    "A reverse charge code (kód předmětu plnění) is required for PDP items.");

            if (item.VatRegime != EVatRegime.ReverseCharge && item.ReverseChargeCodeId.HasValue)
                throw new InvalidOperationException(
                    $"Invoice item '{item.Description}' has ReverseChargeCodeId set but VatRegime={item.VatRegime}. " +
                    "ReverseChargeCodeId must only be set for ReverseCharge items.");
        }
    }

    /// <summary>
    /// Calculates VatAmount, InformationalVatAmount, and TotalWithVat for an invoice item,
    /// applying the correct logic per the item's VatRegime.
    ///
    /// Regime rules:
    /// - Standard:       VatAmount = TotalBeforeVat * Rate / 100 (rounded AwayFromZero).
    ///                   TotalWithVat = TotalBeforeVat + VatAmount.
    ///                   InformationalVatAmount = 0.
    /// - ReverseCharge:  VatAmount = 0 (not billed — buyer self-assesses).
    ///                   TotalWithVat = TotalBeforeVat (no VAT added to invoice).
    ///                   InformationalVatAmount = TotalBeforeVat * Rate / 100 (shown on PDF/ISDOC
    ///                   so buyer knows what to self-assess, per §92a ZDPH).
    /// - Exempt / OutOfScope: VatAmount = 0, TotalWithVat = TotalBeforeVat,
    ///                        InformationalVatAmount = 0.
    ///
    /// TotalBeforeVat must be set on the item before calling this method.
    /// </summary>
    private static void CalculateItemVat(InvoiceItem item)
    {
        switch (item.VatRegime)
        {
            case EVatRegime.Standard:
                // Normal VAT: supplier charges and remits VAT.
                item.VatAmount = Math.Round(
                    item.TotalBeforeVat * (item.VatRatePercentage / 100m),
                    2, MidpointRounding.AwayFromZero);
                item.TotalWithVat = item.TotalBeforeVat + item.VatAmount;
                item.InformationalVatAmount = 0;
                break;

            case EVatRegime.ReverseCharge:
                // PDP: buyer self-assesses VAT — supplier bills 0 VAT.
                if (item.VatRatePercentage <= 0m)
                    throw new InvalidOperationException(
                        $"Invoice item '{item.Description}' uses reverse charge, so it needs the VAT rate that applies " +
                        "in the Czech Republic (VatRatePercentage > 0).");
                // The rate and the "would-be" VAT amount are displayed informatively on the document.
                item.VatAmount = 0;
                item.TotalWithVat = item.TotalBeforeVat;
                item.InformationalVatAmount = Math.Round(
                    item.TotalBeforeVat * (item.VatRatePercentage / 100m),
                    2, MidpointRounding.AwayFromZero);
                break;

            case EVatRegime.Exempt:
            case EVatRegime.OutOfScope:
                // No VAT charged, no informational amount.
                item.VatAmount = 0;
                item.TotalWithVat = item.TotalBeforeVat;
                item.InformationalVatAmount = 0;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(item.VatRegime),
                    item.VatRegime, "Unhandled VatRegime value in CalculateItemVat.");
        }
    }

    /// <summary>
    /// Calculates due date based on client's billing settings, respecting EDueDateCalculationType.
    /// Uses DueDateCalculator shared helper to ensure consistent calculation across backend and UI.
    ///
    /// If the client has BillingSettings, uses their DueDateCalculationType + DueDays.
    /// Otherwise falls back to DaysFromIssue with 14 days.
    ///
    /// If the DTO already has an explicit DueDate set by the user, that value is returned as-is
    /// (manual override takes priority over automatic calculation).
    /// </summary>
    private DateTime CalculateDueDate(CreateInvoiceDto createDto, Client client)
    {
        // If user explicitly set a due date, respect their manual override
        // Normalize to UTC midnight to prevent timezone day shifts.
        if (createDto.DueDate.HasValue)
            return NormalizeToUtcMidnight(createDto.DueDate)!.Value;

        var issueDate = NormalizeToUtcMidnight(createDto.IssueDate) ?? DateTime.UtcNow.Date;

        // Use client's billing settings if available (DueDateCalculationType + DueDays)
        if (client.BillingSettings != null)
        {
            return NormalizeToUtcMidnight(DueDateCalculator.Calculate(
                issueDate,
                client.BillingSettings.DueDays,
                client.BillingSettings.DueDateCalculationType))!.Value;
        }

        // Default: DaysFromIssue with 14 days (when client has no billing settings)
        return NormalizeToUtcMidnight(DueDateCalculator.Calculate(
            issueDate,
            dueDays: 14,
            EDueDateCalculationType.DaysFromIssue))!.Value;
    }

    /// <summary>
    /// Normalizes a date-only value to UTC midnight — strips time and sets Kind=Utc.
    ///
    /// Why this is needed:
    /// Blazor WASM date pickers may produce DateTime with Kind=Local (e.g., DateTime.Now).
    /// System.Text.Json serializes Local as "2026-05-01T00:00:00+02:00" (with TZ offset).
    /// The server (Azure, UTC) deserializes this as "2026-04-30T22:00:00" — the PREVIOUS day.
    /// Simply calling .Date would preserve this shifted date (April 30 instead of May 1).
    ///
    /// Fix: If the time component is >= 22:00 (indicating a midnight+offset conversion for
    /// European timezones UTC+1..+2), round UP to the next day. Otherwise use .Date as-is.
    /// This handles the common case where a date-only value was shifted back by 1-2 hours.
    /// </summary>
    private static DateTime? NormalizeToUtcMidnight(DateTime? date)
    {
        if (!date.HasValue) return null;
        var dt = date.Value;

        // If time is very close to midnight of the NEXT day (22:00-23:59 UTC),
        // this was likely a midnight local time converted to UTC with negative offset.
        // Round up to next day to recover the intended calendar date.
        if (dt.Hour >= 22)
        {
            return DateTime.SpecifyKind(dt.Date.AddDays(1), DateTimeKind.Utc);
        }

        return DateTime.SpecifyKind(dt.Date, DateTimeKind.Utc);
    }

    /// <summary>
    /// Generates document number using number sequence service.
    /// Priority: 1) explicit overrideSequenceId (from template), 2) issuer's custom sequence, 3) default for doc type.
    /// </summary>
    /// <summary>
    /// Attempts to release the document number of a deleted invoice back to the number sequence.
    ///
    /// Extracts the counter value from the document number and asks the sequence service
    /// to decrement if this was the last generated number. If another invoice was created
    /// after this one, the release is silently skipped (no gap in the middle allowed).
    ///
    /// This is best-effort — failures are logged but do not block the deletion.
    /// </summary>
    private async Task TryReleaseDocumentNumberAsync(Invoice invoice, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(invoice.DocumentNumber) || invoice.DocumentNumber == "DRAFT")
            return;

        try
        {
            // Find the default sequence for this document type to get the current counter.
            var sequence = await _context.NumberSequence
                .AsNoTracking()
                .OrderBy(s => s.Id)
                .FirstOrDefaultAsync(
                    s => s.DocumentType == invoice.DocumentType && s.IsDefault && s.IsActive, ct);

            if (sequence == null)
                return;

            // The CurrentNumber in the sequence is the NEXT number to be generated.
            // If the deleted invoice was the last one, releasing means decrementing by 1.
            // We pass CurrentNumber as the expected value — the service only decrements
            // if it still matches (no other invoice was generated since).
            var released = await _numberSequenceService.TryReleaseLastNumberAsync(
                invoice.DocumentType, sequence.CurrentNumber, ct);

            if (released)
            {
                _logger.LogInformation(
                    "Released document number '{DocNum}' for {DocumentType} — counter decremented, will be reused",
                    invoice.DocumentNumber, invoice.DocumentType);
                // DocumentNumber is cleared by the caller (DeleteInvoiceAsync) after this method returns.
            }
            else
            {
                _logger.LogDebug(
                    "Document number '{DocNum}' NOT released — not the last in sequence",
                    invoice.DocumentNumber);
            }
        }
        catch (Exception ex)
        {
            // Best-effort — don't fail the deletion if number release fails.
            _logger.LogWarning(ex, "Failed to release document number '{DocNum}'", invoice.DocumentNumber);
        }
    }

    private async Task<string> GenerateDocumentNumberAsync(
        Invoice invoice, CancellationToken cancellationToken, long? overrideSequenceId = null)
    {
        _logger.LogInformation("Generating document number for {DocumentType} invoice {Id}", invoice.DocumentType, invoice.Id);

        // Load the CLIENT (customer) with billing settings to check for custom sequences
        // and client-specific prefix/suffix.
        // BillingSettings defines how invoices should be generated FOR this particular client
        // (e.g., "-EU" suffix for EU clients, custom sequence for export invoices).
        //
        // AsNoTracking() is REQUIRED here — without it, EF Core identity resolution returns the
        // already-tracked Client instance from the current DbContext scope. If that instance was
        // loaded earlier (e.g., via FindAsync in CreateInvoiceAsync or
        // InvoiceTemplateService.CreateInvoiceFromTemplateAsync) without the BillingSettings
        // Include, the navigation property stays null on the cached entity even after this
        // Include() call — because EF InMemory performs eager fixup while PostgreSQL does not
        // populate navigation properties on already-tracked entities that were fetched without
        // the relevant Include. AsNoTracking() forces a fresh SQL query that always returns the
        // BillingSettings row regardless of what is in the identity map.
        var client = await _context.Client
            .AsNoTracking()
            .Include(c => c.BillingSettings)
            .FirstOrDefaultAsync(c => c.Id == invoice.ClientId, cancellationToken);

        // ── Phase 1: Sequence resolution ──────────────────────────────────────
        // Priority (highest wins):
        //   1) overrideSequenceId from template (explicit template override)
        //   2) client.BillingSettings.Custom*SequenceId (per-client custom sequence)
        //   3) default sequence for DocumentType (fallback — no custom sequence set)
        //
        // This phase answers: "which counter do we increment?"
        long? resolvedSequenceId = null;

        if (overrideSequenceId.HasValue)
        {
            // Template explicitly specifies a sequence — use it as-is.
            resolvedSequenceId = overrideSequenceId.Value;
            _logger.LogInformation("Using template-override sequence {SequenceId} for {DocumentType}",
                resolvedSequenceId.Value, invoice.DocumentType);
        }
        else if (client?.BillingSettings != null)
        {
            // No template override — fall back to client's custom sequence (if any).
            resolvedSequenceId = invoice.DocumentType == EDocumentType.Invoice
                ? client.BillingSettings.CustomInvoiceNumberSequenceId
                : invoice.DocumentType == EDocumentType.CreditNote
                    ? client.BillingSettings.CustomCreditNoteNumberSequenceId
                    : null;

            if (resolvedSequenceId.HasValue)
                _logger.LogInformation("Using client-custom sequence {SequenceId} for {DocumentType}",
                    resolvedSequenceId.Value, invoice.DocumentType);
        }

        // ── Phase 2: Prefix/suffix resolution ────────────────────────────────
        // Client prefix/suffix is ALWAYS applied when BillingSettings is present,
        // regardless of which sequence was chosen in Phase 1.
        // Rationale: the sequence decides the counter, but the client label
        // (e.g. "-EU" suffix) is a per-customer concern — orthogonal to the sequence.
        string? customPrefix = null;
        string? customSuffix = null;

        if (client?.BillingSettings != null)
        {
            if (invoice.DocumentType == EDocumentType.Invoice)
            {
                customPrefix = client.BillingSettings.InvoiceNumberPrefix;
                customSuffix = client.BillingSettings.InvoiceNumberSuffix;
            }
            else if (invoice.DocumentType == EDocumentType.CreditNote)
            {
                customPrefix = client.BillingSettings.CreditNoteNumberPrefix;
                customSuffix = client.BillingSettings.CreditNoteNumberSuffix;
            }
        }

        try
        {
            string number;

            if (resolvedSequenceId.HasValue)
            {
                // Generate the bare number from the chosen sequence.
                // Prefix/suffix from the client (Phase 2) are applied below, outside this branch,
                // so both "custom sequence" and "default sequence" paths share the same wrapping.
                number = await _numberSequenceService.GenerateNextNumberAsync(
                    resolvedSequenceId.Value,
                    invoice.IssueDate ?? DateTime.UtcNow,
                    cancellationToken);
            }
            else
            {
                // No explicit sequence — use the default sequence for the document type.
                // Pass prefix/suffix into this call so the sequence service can embed them
                // in the generated format string (it may include them in the pattern itself).
                _logger.LogInformation("Using default sequence for {DocumentType}", invoice.DocumentType);

                number = await _numberSequenceService.GenerateNextNumberForDocumentTypeAsync(
                    invoice.DocumentType,
                    invoice.IssueDate ?? DateTime.UtcNow,
                    customPrefix,
                    customSuffix,
                    cancellationToken);

                // Prefix/suffix already passed into GenerateNextNumberForDocumentTypeAsync;
                // skip the manual wrapping below to avoid double-application.
                return number;
            }

            // Apply client prefix/suffix to the bare number produced by a named sequence
            // (GenerateNextNumberAsync does not embed prefix/suffix itself).
            if (!string.IsNullOrEmpty(customPrefix))
                number = customPrefix + number;
            if (!string.IsNullOrEmpty(customSuffix))
                number = number + customSuffix;

            return number;
        }
        catch (InvalidOperationException ex) when (ex.InnerException is DbUpdateConcurrencyException)
        {
            // Issue #155, transient case: concurrent requests exhausted the optimistic-concurrency
            // retry budget inside NumberSequenceService.GenerateNextNumberAsync. That method wraps
            // the last DbUpdateConcurrencyException into an InvalidOperationException, and the inner
            // type is what distinguishes this case from a configuration problem.
            //
            // Nothing is misconfigured here, so pointing the user at /number-sequences would be
            // misleading advice — the correct instruction is simply to repeat the action.
            _logger.LogError(ex,
                "Document number generation hit a concurrency collision for {DocumentType} {Id}: {Message}",
                invoice.DocumentType, invoice.Id, ex.Message);

            throw new InvalidOperationException(
                $"Cannot generate a document number for {invoice.DocumentType} right now — another " +
                "request was drawing a number from the same sequence at the same moment " +
                $"({ex.Message}). Nothing is misconfigured; please repeat the action.",
                ex);
        }
        catch (InvalidOperationException ex)
        {
            // Issue #155: a failed number generation is an ERROR, never a silent fallback.
            //
            // The previous implementation caught this exception and returned a hardcoded
            // "INV{year}{counter}" number. That number ignored the configured series, its
            // prefix and its format, and the user was never told — only a warning was logged.
            // For accounting documents that is unacceptable: the series must stay continuous
            // and predictable, because that is what the accountant reconciles against.
            //
            // This branch handles the CONFIGURATION failures reported by INumberSequenceService:
            // the series is missing or it is inactive. (The third failure path — an exhausted
            // concurrency retry budget — is transient and handled by the catch block above,
            // which is selected by the DbUpdateConcurrencyException carried as InnerException.)
            // We rethrow with a message that tells the user WHAT is wrong and WHERE to fix it,
            // keeping the original error as InnerException for diagnostics.
            //
            // The exception type stays InvalidOperationException on purpose: the API
            // controllers already translate it into HTTP 400 with the message passed through
            // to the UI, so the user sees the actionable text instead of a generic 500.
            _logger.LogError(ex,
                "Document number generation failed for {DocumentType} {Id}: {Message}",
                invoice.DocumentType, invoice.Id, ex.Message);

            throw new InvalidOperationException(
                $"Cannot generate a document number for {invoice.DocumentType} — the number sequence " +
                $"is missing, inactive or unusable ({ex.Message}). " +
                "Set up an active default number sequence for this document type on the " +
                "/number-sequences page and try again.",
                ex);
        }
    }

}
