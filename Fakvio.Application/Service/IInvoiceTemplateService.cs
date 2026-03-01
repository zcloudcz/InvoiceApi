using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.InvoiceTemplate;
using Fakvio.Domain.Enums;

namespace Fakvio.Application.Service;

/// <summary>
/// Service interface for invoice template management
/// </summary>
public interface IInvoiceTemplateService
{
    /// <summary>
    /// Get all active templates
    /// Used for template selection in UI
    /// </summary>
    Task<List<InvoiceTemplateDto>> GetActiveTemplatesAsync(
        EDocumentType? documentType = null,
        string? category = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get templates with pagination, filtering, and sorting
    /// </summary>
    Task<PagedResult<InvoiceTemplateDto>> GetTemplatesPagedAsync(
        int page = 1,
        int pageSize = 10,
        string? search = null,
        EDocumentType? documentType = null,
        string? category = null,
        bool? isActive = null,
        long? issuerId = null,
        string sortBy = "Name",
        bool isDescending = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get template by ID
    /// </summary>
    Task<InvoiceTemplateDto?> GetTemplateByIdAsync(long templateId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create a new invoice template
    /// </summary>
    Task<InvoiceTemplateDto> CreateTemplateAsync(CreateInvoiceTemplateDto createDto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update an existing invoice template
    /// </summary>
    Task<InvoiceTemplateDto?> UpdateTemplateAsync(long templateId, UpdateInvoiceTemplateDto updateDto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete (soft delete - set IsActive = false) an invoice template
    /// </summary>
    Task<bool> DeleteTemplateAsync(long templateId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create an invoice from a template
    /// Copies template data and allows client-specific customization
    /// </summary>
    Task<InvoiceDto> CreateInvoiceFromTemplateAsync(
        long templateId,
        CreateInvoiceFromTemplateDto createDto,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Create a template from an existing invoice
    /// Useful for saving frequently used invoice configurations
    /// </summary>
    Task<InvoiceTemplateDto> CreateTemplateFromInvoiceAsync(
        long invoiceId,
        string templateName,
        string? description = null,
        string? category = null,
        CancellationToken cancellationToken = default);
}
