using System.Text;
using InvoiceApi.Application.Dto.Invoice;
using InvoiceApi.BlazorUI.Models;
using InvoiceApi.Domain.Enums;
using Microsoft.AspNetCore.Components.Authorization;

// BulkOperationRequest, BulkOperationResult, BulkOperationError are in the Invoice DTO namespace

namespace InvoiceApi.BlazorUI.Services;

/// <summary>
/// Blazor service for communicating with the Invoice API endpoints.
/// Inherits ApiClientBase for shared auth, logging, impersonation, and error handling.
/// All HTTP calls go through the base class methods — no manual header management needed.
/// </summary>
public class InvoiceApiService : ApiClientBase
{
    public InvoiceApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<InvoiceApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Gets all invoices (non-paginated).
    /// </summary>
    public async Task<List<InvoiceDto>> GetAllAsync()
    {
        var result = await GetAsync<List<InvoiceDto>>("/api/invoice");
        return result ?? new List<InvoiceDto>();
    }

    /// <summary>
    /// Gets invoices with server-side pagination, filtering, and sorting.
    /// Builds a query string from all the optional filter parameters.
    /// </summary>
    public async Task<PagedResult<InvoiceDto>> GetPagedAsync(
        int page = 1,
        int pageSize = 50,
        string? search = null,
        string? sortBy = null,
        string? sortDirection = "asc",
        EDocumentType? documentType = null,
        EInvoiceStatus? status = null,
        long? clientId = null,
        long? issuerId = null,
        DateTime? issueDateFrom = null,
        DateTime? issueDateTo = null,
        DateTime? dueDateFrom = null,
        DateTime? dueDateTo = null,
        bool? isOverdue = null,
        string? currency = null,
        decimal? minAmount = null,
        decimal? maxAmount = null)
    {
        // Build query string from all provided filter parameters
        var queryParams = new StringBuilder($"?Page={page}&PageSize={pageSize}");

        if (!string.IsNullOrWhiteSpace(search))
            queryParams.Append($"&Search={Uri.EscapeDataString(search)}");

        if (!string.IsNullOrWhiteSpace(sortBy))
            queryParams.Append($"&SortBy={sortBy}");

        if (!string.IsNullOrWhiteSpace(sortDirection))
            queryParams.Append($"&SortDirection={sortDirection}");

        if (documentType.HasValue)
            queryParams.Append($"&DocumentType={documentType.Value}");

        if (status.HasValue)
            queryParams.Append($"&Status={status.Value}");

        if (clientId.HasValue)
            queryParams.Append($"&ClientId={clientId.Value}");

        if (issuerId.HasValue)
            queryParams.Append($"&IssuerId={issuerId.Value}");

        if (issueDateFrom.HasValue)
            queryParams.Append($"&IssueDateFrom={issueDateFrom.Value:yyyy-MM-dd}");

        if (issueDateTo.HasValue)
            queryParams.Append($"&IssueDateTo={issueDateTo.Value:yyyy-MM-dd}");

        if (dueDateFrom.HasValue)
            queryParams.Append($"&DueDateFrom={dueDateFrom.Value:yyyy-MM-dd}");

        if (dueDateTo.HasValue)
            queryParams.Append($"&DueDateTo={dueDateTo.Value:yyyy-MM-dd}");

        if (isOverdue.HasValue)
            queryParams.Append($"&IsOverdue={isOverdue.Value}");

        if (!string.IsNullOrWhiteSpace(currency))
            queryParams.Append($"&Currency={Uri.EscapeDataString(currency)}");

        if (minAmount.HasValue)
            queryParams.Append($"&MinAmount={minAmount.Value}");

        if (maxAmount.HasValue)
            queryParams.Append($"&MaxAmount={maxAmount.Value}");

        var result = await GetAsync<PagedResult<InvoiceDto>>($"/api/invoice/paged{queryParams}");
        return result ?? new PagedResult<InvoiceDto>();
    }

    /// <summary>
    /// Gets a single invoice by ID, including line items.
    /// </summary>
    public async Task<InvoiceDto?> GetByIdAsync(long id)
    {
        return await GetAsync<InvoiceDto>($"/api/invoice/{id}");
    }

    /// <summary>
    /// Creates a new invoice (starts as Draft).
    /// </summary>
    public async Task<InvoiceDto?> CreateAsync(CreateInvoiceDto createDto)
    {
        return await PostAsync<CreateInvoiceDto, InvoiceDto>("/api/invoice", createDto);
    }

    /// <summary>
    /// Updates an existing draft invoice.
    /// </summary>
    public async Task<InvoiceDto?> UpdateAsync(long id, UpdateInvoiceDto updateDto)
    {
        return await PutAsync<UpdateInvoiceDto, InvoiceDto>($"/api/invoice/{id}", updateDto);
    }

    /// <summary>
    /// Deletes an invoice (soft delete).
    /// </summary>
    public async Task<bool> DeleteAsync(long id)
    {
        return await DeleteAsync($"/api/invoice/{id}");
    }

    /// <summary>
    /// Downloads the invoice as a PDF byte array using the default template.
    /// Uses GetBytesAsync from the base class for binary content.
    /// </summary>
    public async Task<byte[]?> ExportToPdfAsync(long id)
    {
        return await GetBytesAsync($"/api/invoice/{id}/pdf");
    }

    /// <summary>
    /// Downloads the invoice as a PDF byte array using a specific content template.
    /// When templateId is null, uses the default template for the document type.
    /// When provided, uses the specified template — allows users to choose PDF layouts.
    /// </summary>
    public async Task<byte[]?> ExportToPdfAsync(long id, long? templateId)
    {
        var url = templateId.HasValue
            ? $"/api/invoice/{id}/pdf?templateId={templateId.Value}"
            : $"/api/invoice/{id}/pdf";
        return await GetBytesAsync(url);
    }

    /// <summary>
    /// Downloads the QR code image (PNG) for the given invoice.
    /// Returns a combined QR Platba+F if the invoice has an IBAN,
    /// or QR Faktura only if no IBAN is available.
    /// </summary>
    /// <param name="id">Invoice ID</param>
    /// <param name="size">QR module size in pixels (5-20, default 10)</param>
    public async Task<byte[]?> GetQrCodeAsync(long id, int size = 10)
    {
        return await GetBytesAsync($"/api/invoice/{id}/qr?size={size}");
    }

    /// <summary>
    /// Completes (issues) a draft invoice — changes status to Completed.
    /// Maps to POST /api/invoice/{id}/complete on the backend.
    /// Uses PostWithoutBodyAsync because the endpoint only needs the invoice ID in the URL.
    /// </summary>
    public async Task<InvoiceDto?> IssueAsync(long id)
    {
        return await PostWithoutBodyAsync<InvoiceDto>($"/api/invoice/{id}/complete");
    }

    /// <summary>
    /// Marks a completed invoice as paid.
    /// Maps to POST /api/invoice/{id}/mark-paid on the backend.
    /// </summary>
    public async Task<InvoiceDto?> MarkAsPaidAsync(long id)
    {
        return await PostWithoutBodyAsync<InvoiceDto>($"/api/invoice/{id}/mark-paid");
    }

    /// <summary>
    /// Sends the invoice as an email with PDF attachment to the specified recipient.
    /// The backend generates the PDF and sends it via configured SMTP.
    /// </summary>
    public async Task<bool> SendEmailAsync(long invoiceId, string recipientEmail, string? subject = null, string? message = null)
    {
        var dto = new { RecipientEmail = recipientEmail, Subject = subject, Message = message };
        return await PostBoolAsync($"/api/invoice/{invoiceId}/send-email", dto);
    }

    // ─── Bulk Operations ─────────────────────────────────────────────────────

    /// <summary>
    /// Completes (issues) multiple draft invoices in a single batch via POST /api/invoice/bulk/complete.
    /// Returns a result with success/failure counts and error details for partial failures.
    /// </summary>
    public async Task<BulkOperationResult?> BulkCompleteAsync(List<long> invoiceIds)
    {
        var request = new BulkOperationRequest { InvoiceIds = invoiceIds };
        return await PostAsync<BulkOperationRequest, BulkOperationResult>("/api/invoice/bulk/complete", request);
    }

    /// <summary>
    /// Marks multiple completed invoices as paid in a single batch via POST /api/invoice/bulk/mark-paid.
    /// Returns a result with success/failure counts and error details for partial failures.
    /// </summary>
    public async Task<BulkOperationResult?> BulkMarkAsPaidAsync(List<long> invoiceIds)
    {
        var request = new BulkOperationRequest { InvoiceIds = invoiceIds };
        return await PostAsync<BulkOperationRequest, BulkOperationResult>("/api/invoice/bulk/mark-paid", request);
    }

    /// <summary>
    /// Soft-deletes multiple draft invoices in a single batch via POST /api/invoice/bulk/delete.
    /// Returns a result with success/failure counts and error details for partial failures.
    /// </summary>
    public async Task<BulkOperationResult?> BulkDeleteAsync(List<long> invoiceIds)
    {
        var request = new BulkOperationRequest { InvoiceIds = invoiceIds };
        return await PostAsync<BulkOperationRequest, BulkOperationResult>("/api/invoice/bulk/delete", request);
    }

    /// <summary>
    /// Sends invoice emails for multiple invoices in a single batch via POST /api/invoice/bulk/send-email.
    /// Each email is sent to the invoice's client default email address.
    /// </summary>
    public async Task<BulkOperationResult?> BulkSendEmailAsync(List<long> invoiceIds)
    {
        var request = new BulkOperationRequest { InvoiceIds = invoiceIds };
        return await PostAsync<BulkOperationRequest, BulkOperationResult>("/api/invoice/bulk/send-email", request);
    }

    /// <summary>
    /// Downloads a ZIP archive containing PDFs for multiple invoices.
    /// Returns the ZIP as a byte array for client-side download via JS interop.
    /// </summary>
    public async Task<byte[]?> BulkExportPdfAsync(List<long> invoiceIds)
    {
        var idsParam = string.Join(",", invoiceIds);
        return await GetBytesAsync($"/api/invoice/bulk/pdf?ids={idsParam}");
    }
}
