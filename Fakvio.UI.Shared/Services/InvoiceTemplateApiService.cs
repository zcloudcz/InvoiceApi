using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.InvoiceTemplate;
using Fakvio.UI.Shared.Models;
using Fakvio.Domain.Enums;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor service for communicating with the InvoiceTemplate API endpoints.
/// Inherits ApiClientBase for shared auth, logging, and error handling.
/// All HTTP calls go through the base class methods — no manual header management needed.
/// </summary>
public class InvoiceTemplateApiService : ApiClientBase
{
    public InvoiceTemplateApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<InvoiceTemplateApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Gets templates with server-side pagination, filtering, and sorting.
    /// Used by the InvoiceTemplates page grid.
    /// </summary>
    public async Task<PagedResult<InvoiceTemplateDto>> GetPagedAsync(
        int page = 1,
        int pageSize = 10,
        string? search = null,
        EDocumentType? documentType = null,
        bool? isActive = null,
        string sortBy = "Name",
        bool isDescending = false)
    {
        try
        {
            var endpoint = $"/api/invoicetemplate?page={page}&pageSize={pageSize}" +
                           $"&search={Uri.EscapeDataString(search ?? "")}" +
                           $"&documentType={documentType}" +
                           $"&isActive={isActive}" +
                           $"&sortBy={sortBy}&isDescending={isDescending}";

            return await GetAsync<PagedResult<InvoiceTemplateDto>>(endpoint)
                   ?? new PagedResult<InvoiceTemplateDto>();
        }
        catch (ApiException)
        {
            // Graceful degradation for list endpoints — show empty grid instead of crashing.
            // 401 is already handled by UnauthorizedRedirectHandler (redirects to /login).
            return new PagedResult<InvoiceTemplateDto>();
        }
    }

    /// <summary>
    /// Gets all active templates (non-paginated).
    /// Used for dropdowns and template selection dialogs.
    /// Fetches up to 200 templates — sufficient for selection UI.
    /// </summary>
    public async Task<List<InvoiceTemplateDto>> GetAllActiveAsync()
    {
        var result = await GetPagedAsync(page: 1, pageSize: 200, isActive: true);
        return result.Items?.ToList() ?? [];
    }

    /// <summary>
    /// Gets a single template by ID, including line items.
    /// Returns null if not found; throws on server errors with details.
    /// </summary>
    public async Task<InvoiceTemplateDto?> GetByIdAsync(long id)
    {
        return await GetAsync<InvoiceTemplateDto>($"/api/invoicetemplate/{id}");
    }

    /// <summary>
    /// Creates a new invoice template.
    /// Throws ApiException with error details if the API returns non-success.
    /// </summary>
    public async Task<InvoiceTemplateDto?> CreateAsync(CreateInvoiceTemplateDto dto)
    {
        return await PostAsync<CreateInvoiceTemplateDto, InvoiceTemplateDto>("/api/invoicetemplate", dto);
    }

    /// <summary>
    /// Updates an existing invoice template.
    /// Throws ApiException with error details if the API returns non-success.
    /// </summary>
    public async Task<InvoiceTemplateDto?> UpdateAsync(long id, UpdateInvoiceTemplateDto dto)
    {
        return await PutAsync<UpdateInvoiceTemplateDto, InvoiceTemplateDto>($"/api/invoicetemplate/{id}", dto);
    }

    /// <summary>
    /// Deletes an invoice template (soft delete).
    /// </summary>
    public async Task<bool> DeleteAsync(long id)
    {
        return await DeleteAsync($"/api/invoicetemplate/{id}");
    }

    /// <summary>
    /// Creates a new invoice from an existing template.
    /// Calls POST /api/invoicetemplate/{templateId}/create-invoice.
    /// Returns the created InvoiceDto on success.
    /// </summary>
    public async Task<InvoiceDto?> CreateInvoiceFromTemplateAsync(
        long templateId, CreateInvoiceFromTemplateDto dto)
    {
        return await PostAsync<CreateInvoiceFromTemplateDto, InvoiceDto>(
            $"/api/invoicetemplate/{templateId}/create-invoice", dto);
    }

    /// <summary>
    /// Creates a new invoice template from an existing invoice.
    /// Calls POST /api/invoicetemplate/from-invoice/{invoiceId}.
    /// </summary>
    public async Task<InvoiceTemplateDto?> CreateFromInvoiceAsync(
        long invoiceId, string templateName, string? description = null, string? category = null)
    {
        var request = new CreateTemplateFromInvoiceDto
        {
            TemplateName = templateName,
            Description = description,
            Category = category
        };

        return await PostAsync<CreateTemplateFromInvoiceDto, InvoiceTemplateDto>(
            $"/api/invoicetemplate/from-invoice/{invoiceId}", request);
    }
}
