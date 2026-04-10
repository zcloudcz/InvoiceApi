using Fakvio.Application.Common.Extensions;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.InvoiceTemplate;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZMapper;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Implementation of invoice template service
/// Handles all business logic for invoice templates
/// </summary>
public class InvoiceTemplateService : IInvoiceTemplateService
{
    private readonly TenantDbContext _context;
    private readonly IInvoiceService _invoiceService;
    private readonly ILogger<InvoiceTemplateService> _logger;

    public InvoiceTemplateService(
        TenantDbContext context,
        IInvoiceService invoiceService,
        ILogger<InvoiceTemplateService> logger)
    {
        _context = context;
        _invoiceService = invoiceService;
        _logger = logger;
    }

    /// <summary>
    /// Maps an InvoiceTemplate entity to InvoiceTemplateDto using ZMapper v1.1.0.
    /// ZMapper now handles BaseEntity and nested Ids automatically.
    /// Only navigation-derived properties must be set manually.
    /// </summary>
    private static InvoiceTemplateDto MapToDto(InvoiceTemplate entity)
    {
        var dto = entity.ToInvoiceTemplateDto();

        // Navigation-derived properties — ZMapper cannot flatten navigation paths
        dto.IssuerName = entity.Issuer?.CompanyName ?? string.Empty;
        dto.ClientName = entity.Client?.CompanyName;
        dto.CurrencyCode = entity.Currency?.Code ?? string.Empty;
        dto.CurrencySymbol = entity.Currency?.Symbol ?? string.Empty;
        dto.NumberSequenceName = entity.NumberSequence?.Name;

        return dto;
    }

    public async Task<List<InvoiceTemplateDto>> GetActiveTemplatesAsync(
        EDocumentType? documentType = null,
        string? category = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching active templates");

        // AsNoTracking: read-only list — results are mapped to DTOs
        var query = _context.Set<InvoiceTemplate>()
            .AsNoTracking()
            .Include(t => t.Issuer)
            .Include(t => t.Client)
            .Include(t => t.Currency)
            .Include(t => t.InvoiceItem.OrderBy(item => item.OrderIndex))
            .Include(t => t.NumberSequence)
            .Where(t => t.IsActive)
            .AsQueryable();

        if (documentType.HasValue)
            query = query.Where(t => t.DocumentType == documentType.Value);

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(t => t.Category != null && t.Category.ToLower() == category.ToLower());

        var templates = await query
            .OrderBy(t => t.Name)
            .ToListAsync(cancellationToken);

        return templates.Select(t => MapToDto(t)).ToList();
    }

    public async Task<PagedResult<InvoiceTemplateDto>> GetTemplatesPagedAsync(
        int page = 1,
        int pageSize = 10,
        string? search = null,
        EDocumentType? documentType = null,
        string? category = null,
        bool? isActive = null,
        long? issuerId = null,
        string sortBy = "Name",
        bool isDescending = false,
        DateTime? lastUsedAtFrom = null,
        DateTime? lastUsedAtTo = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching paged templates (Page: {Page}, PageSize: {PageSize}, Search: {Search})",
            page, pageSize, search);

        // AsNoTracking: read-only paged query — results are mapped to DTOs
        var query = _context.Set<InvoiceTemplate>()
            .AsNoTracking()
            .Include(t => t.Issuer)
            .Include(t => t.Client)
            .Include(t => t.Currency)
            .Include(t => t.InvoiceItem.OrderBy(item => item.OrderIndex))
            .Include(t => t.NumberSequence)
            .AsQueryable();

        // Apply filters
        if (documentType.HasValue)
            query = query.Where(t => t.DocumentType == documentType.Value);

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(t => t.Category != null && t.Category.ToLower() == category.ToLower());

        if (isActive.HasValue)
            query = query.Where(t => t.IsActive == isActive.Value);

        if (issuerId.HasValue)
            query = query.Where(t => t.IssuerId == issuerId.Value);

        // Search filter - search across Name, Description, Category
        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.ToLower();
            query = query.Where(t =>
                t.Name.ToLower().Contains(searchLower) ||
                (t.Description != null && t.Description.ToLower().Contains(searchLower)) ||
                (t.Category != null && t.Category.ToLower().Contains(searchLower)));
        }

        // LastUsedAt date range — UTC Kind required by Npgsql for 'timestamp with time zone'
        if (lastUsedAtFrom.HasValue)
            query = query.Where(t => t.LastUsedAt >= DateTime.SpecifyKind(lastUsedAtFrom.Value, DateTimeKind.Utc));

        if (lastUsedAtTo.HasValue)
            query = query.Where(t => t.LastUsedAt <= DateTime.SpecifyKind(lastUsedAtTo.Value, DateTimeKind.Utc));

        // Apply sorting
        var validSortFields = new[] { "Name", "Category", "DocumentType", "UsageCount", "LastUsedAt", "CreatedAt", "UpdatedAt" };
        var sortField = !string.IsNullOrWhiteSpace(sortBy) && validSortFields.Contains(sortBy, StringComparer.OrdinalIgnoreCase)
            ? sortBy : "Name";

        query = query.ApplySorting(sortField, isDescending);

        // Get paged results
        var pagedResult = await query.ToPagedResultAsync(page, pageSize, cancellationToken);

        // Map to DTOs
        return new PagedResult<InvoiceTemplateDto>(
            pagedResult.Items.Select(t => MapToDto(t)).ToList(),
            pagedResult.TotalCount,
            pagedResult.PageNumber,
            pagedResult.PageSize);
    }

    public async Task<InvoiceTemplateDto?> GetTemplateByIdAsync(long templateId, CancellationToken cancellationToken = default)
    {
        // AsNoTracking: read-only lookup — result is mapped to DTO
        var template = await _context.Set<InvoiceTemplate>()
            .AsNoTracking()
            .Include(t => t.Issuer)
            .Include(t => t.Client)
            .Include(t => t.Currency)
            .Include(t => t.InvoiceItem.OrderBy(item => item.OrderIndex))
            .Include(t => t.NumberSequence)
            .FirstOrDefaultAsync(t => t.Id == templateId, cancellationToken);

        return template == null ? null : MapToDto(template);
    }

    public async Task<InvoiceTemplateDto> CreateTemplateAsync(CreateInvoiceTemplateDto createDto, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating new template: {Name}", createDto.Name);

        // Validate issuer exists
        var issuer = await _context.Client.FindAsync(new object[] { createDto.IssuerId }, cancellationToken);
        if (issuer == null || !issuer.IsIssuer)
            throw new InvalidOperationException($"Issuer with ID {createDto.IssuerId} not found or is not marked as issuer");

        // Validate currency exists
        var currency = await _context.Currency.FindAsync(new object[] { createDto.CurrencyId }, cancellationToken);
        if (currency == null)
            throw new InvalidOperationException($"Currency with ID {createDto.CurrencyId} not found");

        // Validate VAT requirements if issuer is VAT payer
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

        // Create template entity
        var template = new InvoiceTemplate
        {
            Name = createDto.Name,
            Description = createDto.Description,
            DocumentType = createDto.DocumentType,
            IssuerId = createDto.IssuerId,
            ClientId = createDto.ClientId,
            DueDateOffsetDays = createDto.DueDateOffsetDays,
            VariableSymbol = createDto.VariableSymbol,
            ConstantSymbol = createDto.ConstantSymbol,
            SpecificSymbol = createDto.SpecificSymbol,
            BankAccountNumber = createDto.BankAccountNumber,
            IBAN = createDto.IBAN,
            SWIFT = createDto.SWIFT,
            PaymentMethod = createDto.PaymentMethod,
            CurrencyId = createDto.CurrencyId,
            Notes = createDto.Notes,
            NumberSequenceId = createDto.NumberSequenceId,
            // NOTE: HtmlTemplate removed — PDF templates now in ContentTemplate system
            IsActive = true,
            UsageCount = 0,
            InvoiceItem = new List<InvoiceItem>()
        };

        // Add template items and calculate totals
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

            template.InvoiceItem.Add(item);

            totalBeforeVat += item.TotalBeforeVat;
            totalVat += item.VatAmount;
        }

        template.TotalBeforeVat = totalBeforeVat;
        template.TotalVat = totalVat;
        template.TotalWithVat = totalBeforeVat + totalVat;

        _context.Set<InvoiceTemplate>().Add(template);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created template '{Name}' with ID {Id}", template.Name, template.Id);

        // Reload with related entities
        return (await GetTemplateByIdAsync(template.Id, cancellationToken))!;
    }

    public async Task<InvoiceTemplateDto?> UpdateTemplateAsync(long templateId, UpdateInvoiceTemplateDto updateDto, CancellationToken cancellationToken = default)
    {
        var template = await _context.Set<InvoiceTemplate>()
            .Include(t => t.InvoiceItem)
            .FirstOrDefaultAsync(t => t.Id == templateId, cancellationToken);

        if (template == null)
            return null;

        _logger.LogInformation("Updating template '{Name}' (ID: {Id})", template.Name, template.Id);

        // Update fields
        if (updateDto.Name != null)
            template.Name = updateDto.Name;

        if (updateDto.Description != null)
            template.Description = updateDto.Description;

        // ClientId uses HasValue because null is a valid value (clear the default client)
        if (updateDto.ClientId.HasValue)
            template.ClientId = updateDto.ClientId.Value == 0 ? null : updateDto.ClientId;

        if (updateDto.DueDateOffsetDays.HasValue)
            template.DueDateOffsetDays = updateDto.DueDateOffsetDays.Value;

        if (updateDto.VariableSymbol != null)
            template.VariableSymbol = updateDto.VariableSymbol;

        if (updateDto.ConstantSymbol != null)
            template.ConstantSymbol = updateDto.ConstantSymbol;

        if (updateDto.SpecificSymbol != null)
            template.SpecificSymbol = updateDto.SpecificSymbol;

        if (updateDto.BankAccountNumber != null)
            template.BankAccountNumber = updateDto.BankAccountNumber;

        if (updateDto.IBAN != null)
            template.IBAN = updateDto.IBAN;

        if (updateDto.SWIFT != null)
            template.SWIFT = updateDto.SWIFT;

        if (updateDto.PaymentMethod != null)
            template.PaymentMethod = updateDto.PaymentMethod;

        if (updateDto.CurrencyId.HasValue)
            template.CurrencyId = updateDto.CurrencyId.Value;

        if (updateDto.Notes != null)
            template.Notes = updateDto.Notes;

        // NumberSequenceId — nullable, so we use HasValue check
        if (updateDto.NumberSequenceId.HasValue)
            template.NumberSequenceId = updateDto.NumberSequenceId.Value == 0 ? null : updateDto.NumberSequenceId;

        // NOTE: HtmlTemplate removed — PDF templates now in ContentTemplate system

        if (updateDto.IsActive.HasValue)
            template.IsActive = updateDto.IsActive.Value;

        // Update items if provided
        if (updateDto.InvoiceItem != null)
        {
            // Get issuer to validate VAT requirements
            var issuer = await _context.Client.FindAsync(new object[] { template.IssuerId }, cancellationToken);
            if (issuer == null)
                throw new InvalidOperationException($"Issuer with ID {template.IssuerId} not found");

            // Validate VAT requirements
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
            _context.InvoiceItem.RemoveRange(template.InvoiceItem);

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

                template.InvoiceItem.Add(item);

                totalBeforeVat += item.TotalBeforeVat;
                totalVat += item.VatAmount;
            }

            template.TotalBeforeVat = totalBeforeVat;
            template.TotalVat = totalVat;
            template.TotalWithVat = totalBeforeVat + totalVat;
        }

        await _context.SaveChangesAsync(cancellationToken);

        return await GetTemplateByIdAsync(template.Id, cancellationToken);
    }

    public async Task<bool> DeleteTemplateAsync(long templateId, CancellationToken cancellationToken = default)
    {
        var template = await _context.Set<InvoiceTemplate>()
            .FirstOrDefaultAsync(t => t.Id == templateId, cancellationToken);

        if (template == null)
            return false;

        _logger.LogInformation("Deleting template '{Name}' (ID: {Id})", template.Name, template.Id);

        // Soft delete - set IsActive = false
        template.IsActive = false;

        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<InvoiceDto> CreateInvoiceFromTemplateAsync(
        long templateId,
        CreateInvoiceFromTemplateDto createDto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating invoice from template {TemplateId} for client {ClientId}", templateId, createDto.ClientId);

        // Load template
        var template = await _context.Set<InvoiceTemplate>()
            .Include(t => t.InvoiceItem)
            .FirstOrDefaultAsync(t => t.Id == templateId, cancellationToken);

        if (template == null)
            throw new InvalidOperationException($"Template with ID {templateId} not found");

        if (!template.IsActive)
            throw new InvalidOperationException($"Template '{template.Name}' is not active");

        // Validate client exists
        var client = await _context.Client.FindAsync(new object[] { createDto.ClientId }, cancellationToken);
        if (client == null)
            throw new InvalidOperationException($"Client with ID {createDto.ClientId} not found");

        // Build CreateInvoiceDto from template
        var issueDate = createDto.IssueDate ?? DateTime.UtcNow;
        var dueDate = createDto.DueDate ?? issueDate.AddDays(template.DueDateOffsetDays);

        var invoiceDto = new CreateInvoiceDto
        {
            DocumentType = template.DocumentType,
            ClientId = createDto.ClientId,
            IssuerId = template.IssuerId,
            IssueDate = issueDate,
            DueDate = dueDate,
            TaxableSupplyDate = createDto.TaxableSupplyDate ?? issueDate,
            VariableSymbol = createDto.VariableSymbol ?? template.VariableSymbol,
            ConstantSymbol = template.ConstantSymbol,
            SpecificSymbol = template.SpecificSymbol,
            BankAccountNumber = template.BankAccountNumber,
            IBAN = template.IBAN,
            SWIFT = template.SWIFT,
            PaymentMethod = template.PaymentMethod,
            CurrencyId = template.CurrencyId,
            Notes = createDto.Notes ?? template.Notes,
            // Pass template's custom number sequence to invoice creation
            NumberSequenceId = template.NumberSequenceId,
            InvoiceItem = template.InvoiceItem.Select(item => new CreateInvoiceItemDto
            {
                OrderIndex = item.OrderIndex,
                Description = item.Description,
                Quantity = item.Quantity,
                Unit = item.Unit,
                UnitPrice = item.UnitPrice,
                VatRateId = item.VatRateId,
                VatRatePercentage = item.VatRatePercentage,
                ProductCode = item.ProductCode,
                Notes = item.Notes
            }).ToList()
        };

        // Create invoice using InvoiceService
        var invoice = await _invoiceService.CreateInvoiceAsync(invoiceDto, cancellationToken);

        // Auto-complete if requested
        if (createDto.AutoComplete)
        {
            invoice = await _invoiceService.CompleteInvoiceAsync(invoice.Id, cancellationToken) ?? invoice;
        }

        // Update template usage statistics
        template.UsageCount++;
        template.LastUsedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created invoice {InvoiceId} from template '{TemplateName}' (ID: {TemplateId})",
            invoice.Id, template.Name, template.Id);

        return invoice;
    }

    public async Task<InvoiceTemplateDto> CreateTemplateFromInvoiceAsync(
        long invoiceId,
        string templateName,
        string? description = null,
        string? category = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating template from invoice {InvoiceId}", invoiceId);

        // Load invoice
        var invoice = await _context.Invoice
            .Include(i => i.InvoiceItem)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);

        if (invoice == null)
            throw new InvalidOperationException($"Invoice with ID {invoiceId} not found");

        // Build CreateInvoiceTemplateDto from invoice
        var templateDto = new CreateInvoiceTemplateDto
        {
            Name = templateName,
            Description = description,
            DocumentType = invoice.DocumentType,
            IssuerId = invoice.IssuerId,
            ClientId = invoice.ClientId,
            DueDateOffsetDays = invoice.IssueDate.HasValue && invoice.DueDate.HasValue
                ? (int)(invoice.DueDate.Value - invoice.IssueDate.Value).TotalDays
                : 14,
            VariableSymbol = invoice.VariableSymbol,
            ConstantSymbol = invoice.ConstantSymbol,
            SpecificSymbol = invoice.SpecificSymbol,
            BankAccountNumber = invoice.BankAccountNumber,
            IBAN = invoice.IBAN,
            SWIFT = invoice.SWIFT,
            PaymentMethod = invoice.PaymentMethod,
            CurrencyId = invoice.CurrencyId,
            Notes = invoice.Notes,
            InvoiceItem = invoice.InvoiceItem.Select(item => new CreateInvoiceItemDto
            {
                OrderIndex = item.OrderIndex,
                Description = item.Description,
                Quantity = item.Quantity,
                Unit = item.Unit,
                UnitPrice = item.UnitPrice,
                VatRateId = item.VatRateId,
                VatRatePercentage = item.VatRatePercentage,
                ProductCode = item.ProductCode,
                Notes = item.Notes
            }).ToList()
        };

        // Create template
        var template = await CreateTemplateAsync(templateDto, cancellationToken);

        _logger.LogInformation("Created template '{TemplateName}' (ID: {TemplateId}) from invoice {InvoiceId}",
            template.Name, template.Id, invoiceId);

        return template;
    }

}
