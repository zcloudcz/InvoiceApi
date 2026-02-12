using InvoiceApi.Application.Common;
using InvoiceApi.Application.Common.Extensions;
using InvoiceApi.Application.Common.Pagination;
using InvoiceApi.Application.Dto.Invoice;
using InvoiceApi.Application.Service;
using InvoiceApi.Domain.Entities;
using InvoiceApi.Domain.Enums;
using InvoiceApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZMapper;

namespace InvoiceApi.Infrastructure.Service;

/// <summary>
/// Implementation of invoice service
/// Handles all business logic for invoices and credit notes
/// </summary>
public class InvoiceService : IInvoiceService
{
    private readonly TenantDbContext _context;
    private readonly INumberSequenceService _numberSequenceService;
    private readonly ILogger<InvoiceService> _logger;

    public InvoiceService(
        TenantDbContext context,
        INumberSequenceService numberSequenceService,
        ILogger<InvoiceService> logger)
    {
        _context = context;
        _numberSequenceService = numberSequenceService;
        _logger = logger;
    }

    /// <summary>
    /// Maps an Invoice entity to InvoiceDto using ZMapper v1.1.0.
    /// ZMapper now handles BaseEntity (Id, CreatedAt, UpdatedAt) and nested collection Ids automatically.
    /// Only navigation-derived properties (flattened from related entities) must be set manually.
    /// </summary>
    private static InvoiceDto MapToDto(Invoice entity)
    {
        var dto = entity.ToInvoiceDto();

        // Navigation-derived properties — ZMapper cannot flatten navigation paths
        // (e.g., entity.Client.CompanyName → dto.ClientName) so we set them manually.
        dto.ClientName = entity.Client?.CompanyName ?? string.Empty;
        dto.IssuerName = entity.Issuer?.CompanyName ?? string.Empty;
        dto.CurrencyCode = entity.Currency?.Code ?? string.Empty;
        dto.CurrencySymbol = entity.Currency?.Symbol ?? string.Empty;
        dto.OriginalInvoiceNumber = entity.OriginalInvoice?.DocumentNumber;

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

        // Date range filters
        if (filter.IssueDateFrom.HasValue)
            query = query.Where(i => i.IssueDate >= filter.IssueDateFrom.Value);

        if (filter.IssueDateTo.HasValue)
            query = query.Where(i => i.IssueDate <= filter.IssueDateTo.Value);

        if (filter.DueDateFrom.HasValue)
            query = query.Where(i => i.DueDate >= filter.DueDateFrom.Value);

        if (filter.DueDateTo.HasValue)
            query = query.Where(i => i.DueDate <= filter.DueDateTo.Value);

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
        var validSortFields = new[] { "DocumentNumber", "IssueDate", "DueDate", "TotalWithVat", "Status", "CreatedAt", "UpdatedAt" };
        var sortBy = !string.IsNullOrWhiteSpace(filter.SortBy) && validSortFields.Contains(filter.SortBy, StringComparer.OrdinalIgnoreCase)
            ? filter.SortBy : "IssueDate";

        query = query.ApplySorting(sortBy, filter.IsDescending);

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
            .Include(i => i.OriginalInvoice)
            .FirstOrDefaultAsync(i => i.Id == invoiceId && i.Status != EInvoiceStatus.Deleted, cancellationToken);

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
            .Include(i => i.OriginalInvoice)
            .FirstOrDefaultAsync(i => i.DocumentNumber == documentNumber && i.Status != EInvoiceStatus.Deleted, cancellationToken);

        return invoice == null ? null : MapToDto(invoice);
    }

    public async Task<InvoiceDto> CreateInvoiceAsync(CreateInvoiceDto createDto, CancellationToken cancellationToken = default)
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

        // Create invoice entity
        var invoice = new Invoice
        {
            DocumentType = createDto.DocumentType,
            Status = EInvoiceStatus.Draft,
            DocumentNumber = createDto.CustomDocumentNumber ?? "DRAFT", // Placeholder — replaced below after save
            IssueDate = createDto.IssueDate ?? DateTime.UtcNow,
            TaxableSupplyDate = createDto.TaxableSupplyDate ?? (createDto.IssueDate ?? DateTime.UtcNow),
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
            var itemsWithoutVatRate = createDto.InvoiceItem.Where(i => !i.VatRateId.HasValue).ToList();
            if (itemsWithoutVatRate.Any())
            {
                throw new InvalidOperationException(
                    "When issuer is a VAT payer, all invoice items must have a VAT rate assigned (VatRateId). " +
                    $"Found {itemsWithoutVatRate.Count} item(s) without VAT rate.");
            }
        }

        // Add invoice items and calculate totals
        decimal totalBeforeVat = 0;
        decimal totalVat = 0;

        foreach (var itemDto in createDto.InvoiceItem)
        {
            // Fetch VAT rate percentage if VatRateId is provided
            decimal vatRatePercentage = itemDto.VatRatePercentage;
            if (itemDto.VatRateId.HasValue)
            {
                var vatRate = await _context.VatRate.FindAsync(new object[] { itemDto.VatRateId.Value }, cancellationToken);
                if (vatRate == null)
                {
                    throw new InvalidOperationException($"VAT rate with ID {itemDto.VatRateId} not found");
                }

                // Use rate from VAT rate entity (overrides any value in DTO)
                vatRatePercentage = vatRate.Rate;
            }

            var item = new InvoiceItem
            {
                OrderIndex = itemDto.OrderIndex,
                Description = itemDto.Description,
                Quantity = itemDto.Quantity,
                Unit = itemDto.Unit,
                UnitPrice = itemDto.UnitPrice,
                VatRateId = itemDto.VatRateId,
                VatRatePercentage = vatRatePercentage,
                ProductCode = itemDto.ProductCode,
                Notes = itemDto.Notes
            };

            // Calculate item totals
            item.TotalBeforeVat = item.Quantity * item.UnitPrice;
            item.VatAmount = item.TotalBeforeVat * (item.VatRatePercentage / 100);
            item.TotalWithVat = item.TotalBeforeVat + item.VatAmount;

            invoice.InvoiceItem.Add(item);

            totalBeforeVat += item.TotalBeforeVat;
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

        // Pre-fill VariableSymbol from digits in the document number.
        // Czech banking requires variable symbol = max 10 digits only.
        // Runs regardless of whether document number was auto-generated or custom-provided.
        if (string.IsNullOrEmpty(invoice.VariableSymbol) && !string.IsNullOrEmpty(invoice.DocumentNumber))
        {
            invoice.VariableSymbol = new string(invoice.DocumentNumber
                .Where(char.IsDigit).Take(10).ToArray());
        }

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created {DocumentType} with ID {Id}, DocumentNumber {DocumentNumber}",
            invoice.DocumentType, invoice.Id, invoice.DocumentNumber);

        // Reload with related entities
        return (await GetInvoiceByIdAsync(invoice.Id, cancellationToken))!;
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
        if (updateDto.DueDate.HasValue)
            invoice.DueDate = updateDto.DueDate.Value;

        if (updateDto.TaxableSupplyDate.HasValue)
            invoice.TaxableSupplyDate = updateDto.TaxableSupplyDate.Value;

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

            // Validate VAT requirements: If issuer is VAT payer, all items must have VatRateId
            if (issuer.IsVatPayer)
            {
                var itemsWithoutVatRate = updateDto.InvoiceItem.Where(i => !i.VatRateId.HasValue).ToList();
                if (itemsWithoutVatRate.Any())
                {
                    throw new InvalidOperationException(
                        "When issuer is a VAT payer, all invoice items must have a VAT rate assigned (VatRateId). " +
                        $"Found {itemsWithoutVatRate.Count} item(s) without VAT rate.");
                }
            }

            // Remove old items
            _context.InvoiceItem.RemoveRange(invoice.InvoiceItem);

            // Add new items
            decimal totalBeforeVat = 0;
            decimal totalVat = 0;

            foreach (var itemDto in updateDto.InvoiceItem)
            {
                // Fetch VAT rate percentage if VatRateId is provided
                decimal vatRatePercentage = itemDto.VatRatePercentage;
                if (itemDto.VatRateId.HasValue)
                {
                    var vatRate = await _context.VatRate.FindAsync(new object[] { itemDto.VatRateId.Value }, cancellationToken);
                    if (vatRate == null)
                    {
                        throw new InvalidOperationException($"VAT rate with ID {itemDto.VatRateId} not found");
                    }

                    // Use rate from VAT rate entity (overrides any value in DTO)
                    vatRatePercentage = vatRate.Rate;
                }

                var item = new InvoiceItem
                {
                    OrderIndex = itemDto.OrderIndex,
                    Description = itemDto.Description,
                    Quantity = itemDto.Quantity,
                    Unit = itemDto.Unit,
                    UnitPrice = itemDto.UnitPrice,
                    VatRateId = itemDto.VatRateId,
                    VatRatePercentage = vatRatePercentage,
                    ProductCode = itemDto.ProductCode,
                    Notes = itemDto.Notes
                };

                // Calculate item totals
                item.TotalBeforeVat = item.Quantity * item.UnitPrice;
                item.VatAmount = item.TotalBeforeVat * (item.VatRatePercentage / 100);
                item.TotalWithVat = item.TotalBeforeVat + item.VatAmount;

                invoice.InvoiceItem.Add(item);

                totalBeforeVat += item.TotalBeforeVat;
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

        // Only draft invoices can be deleted
        if (invoice.Status != EInvoiceStatus.Draft)
            throw new InvalidOperationException("Only draft invoices can be deleted");

        _logger.LogInformation("Deleting {DocumentType} {Id}", invoice.DocumentType, invoice.Id);

        invoice.Status = EInvoiceStatus.Deleted;

        await _context.SaveChangesAsync(cancellationToken);

        return true;
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
            .Include(i => i.OriginalInvoice)
            .Where(i => i.OriginalInvoiceId == invoiceId && i.DocumentType == EDocumentType.CreditNote && i.Status != EInvoiceStatus.Deleted)
            .OrderByDescending(i => i.IssueDate ?? DateTime.MinValue)
            .ToListAsync(cancellationToken);

        return creditNotes.Select(i => MapToDto(i)).ToList();
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
        if (createDto.DueDate.HasValue)
            return createDto.DueDate.Value;

        var issueDate = createDto.IssueDate ?? DateTime.UtcNow;

        // Use client's billing settings if available (DueDateCalculationType + DueDays)
        if (client.BillingSettings != null)
        {
            return DueDateCalculator.Calculate(
                issueDate,
                client.BillingSettings.DueDays,
                client.BillingSettings.DueDateCalculationType);
        }

        // Default: DaysFromIssue with 14 days (when client has no billing settings)
        return DueDateCalculator.Calculate(
            issueDate,
            dueDays: 14,
            EDueDateCalculationType.DaysFromIssue);
    }

    /// <summary>
    /// Generates document number using number sequence service.
    /// Priority: 1) explicit overrideSequenceId (from template), 2) issuer's custom sequence, 3) default for doc type.
    /// </summary>
    private async Task<string> GenerateDocumentNumberAsync(
        Invoice invoice, CancellationToken cancellationToken, long? overrideSequenceId = null)
    {
        _logger.LogInformation("Generating document number for {DocumentType} invoice {Id}", invoice.DocumentType, invoice.Id);

        // Load issuer with billing settings to check for custom sequences
        var issuer = await _context.Client
            .Include(c => c.BillingSettings)
            .FirstOrDefaultAsync(c => c.Id == invoice.IssuerId, cancellationToken);

        string? customPrefix = null;
        string? customSuffix = null;
        long? customSequenceId = null;

        // Priority: 1) explicit override (from template), 2) issuer's billing settings
        if (overrideSequenceId.HasValue)
        {
            customSequenceId = overrideSequenceId.Value;
        }
        else if (issuer?.BillingSettings != null)
        {
            if (invoice.DocumentType == EDocumentType.Invoice)
            {
                customSequenceId = issuer.BillingSettings.CustomInvoiceNumberSequenceId;
                customPrefix = issuer.BillingSettings.InvoiceNumberPrefix;
                customSuffix = issuer.BillingSettings.InvoiceNumberSuffix;
            }
            else if (invoice.DocumentType == EDocumentType.CreditNote)
            {
                customSequenceId = issuer.BillingSettings.CustomCreditNoteNumberSequenceId;
                customPrefix = issuer.BillingSettings.CreditNoteNumberPrefix;
                customSuffix = issuer.BillingSettings.CreditNoteNumberSuffix;
            }
        }

        try
        {
            // Use custom sequence if configured
            if (customSequenceId.HasValue)
            {
                _logger.LogInformation("Using custom sequence {SequenceId} for {DocumentType}",
                    customSequenceId.Value, invoice.DocumentType);

                var number = await _numberSequenceService.GenerateNextNumberAsync(
                    customSequenceId.Value,
                    invoice.IssueDate ?? DateTime.UtcNow,
                    cancellationToken);

                // Apply custom prefix/suffix if specified
                if (!string.IsNullOrEmpty(customPrefix))
                    number = customPrefix + number;
                if (!string.IsNullOrEmpty(customSuffix))
                    number = number + customSuffix;

                return number;
            }
            else
            {
                // Use default sequence for document type
                _logger.LogInformation("Using default sequence for {DocumentType}", invoice.DocumentType);

                return await _numberSequenceService.GenerateNextNumberForDocumentTypeAsync(
                    invoice.DocumentType,
                    invoice.IssueDate ?? DateTime.UtcNow,
                    customPrefix,
                    customSuffix,
                    cancellationToken);
            }
        }
        catch (InvalidOperationException ex)
        {
            // Fallback to simple generation if no sequence is configured
            _logger.LogWarning("Number sequence generation failed, using fallback: {Message}", ex.Message);

            var issueDate = invoice.IssueDate ?? DateTime.UtcNow;
            var year = issueDate.Year;
            var prefix = invoice.DocumentType == EDocumentType.Invoice ? "INV" : "CN";

            // Count existing documents of same type in same year
            var count = await _context.Invoice
                .Where(i => i.DocumentType == invoice.DocumentType &&
                           i.IssueDate.HasValue &&
                           i.IssueDate.Value.Year == year &&
                           i.Status != EInvoiceStatus.Deleted &&
                           i.Id != invoice.Id)
                .CountAsync(cancellationToken);

            return $"{prefix}{year:0000}{(count + 1):000}";
        }
    }

}
