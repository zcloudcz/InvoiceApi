using System.Text;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.UI.Shared.Models;
using Fakvio.Domain.Enums;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor service for communicating with the Received Invoice API endpoints.
/// Handles CRUD operations and status transitions for incoming invoices (expenses).
/// Inherits ApiClientBase for shared auth, logging, impersonation, and error handling.
/// </summary>
public class ReceivedInvoiceApiService : ApiClientBase
{
    public ReceivedInvoiceApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<ReceivedInvoiceApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Gets received invoices with server-side pagination and filtering.
    /// </summary>
    public async Task<PagedResult<ReceivedInvoiceDto>> GetPagedAsync(
        int page = 1,
        int pageSize = 50,
        string? search = null,
        string? sortBy = null,
        string? sortDirection = "desc",
        EReceivedInvoiceStatus? status = null,
        long? supplierId = null,
        DateTime? issueDateFrom = null,
        DateTime? issueDateTo = null,
        bool? isOverdue = null)
    {
        try
        {
            var q = new StringBuilder($"?Page={page}&PageSize={pageSize}");

            if (!string.IsNullOrWhiteSpace(search))
                q.Append($"&Search={Uri.EscapeDataString(search)}");
            if (!string.IsNullOrWhiteSpace(sortBy))
                q.Append($"&SortBy={sortBy}");
            if (!string.IsNullOrWhiteSpace(sortDirection))
                q.Append($"&SortDirection={sortDirection}");
            if (status.HasValue)
                q.Append($"&Status={status.Value}");
            if (supplierId.HasValue)
                q.Append($"&SupplierId={supplierId.Value}");
            if (issueDateFrom.HasValue)
                q.Append($"&IssueDateFrom={issueDateFrom.Value:O}");
            if (issueDateTo.HasValue)
                q.Append($"&IssueDateTo={issueDateTo.Value:O}");
            if (isOverdue.HasValue)
                q.Append($"&IsOverdue={isOverdue.Value.ToString().ToLowerInvariant()}");

            return await GetAsync<PagedResult<ReceivedInvoiceDto>>($"/api/received-invoice/paged{q}")
                ?? new PagedResult<ReceivedInvoiceDto>();
        }
        catch (ApiException)
        {
            return new PagedResult<ReceivedInvoiceDto>();
        }
    }

    /// <summary>
    /// Gets a single received invoice by ID.
    /// </summary>
    public async Task<ReceivedInvoiceDto?> GetByIdAsync(long id)
    {
        return await GetAsync<ReceivedInvoiceDto>($"/api/received-invoice/{id}");
    }

    /// <summary>
    /// Creates a new received invoice.
    /// </summary>
    public async Task<ReceivedInvoiceDto?> CreateAsync(CreateReceivedInvoiceDto dto)
    {
        return await PostAsync<CreateReceivedInvoiceDto, ReceivedInvoiceDto>("/api/received-invoice", dto);
    }

    /// <summary>
    /// Updates an existing received invoice.
    /// </summary>
    public async Task<ReceivedInvoiceDto?> UpdateAsync(long id, UpdateReceivedInvoiceDto dto)
    {
        return await PutAsync<UpdateReceivedInvoiceDto, ReceivedInvoiceDto>($"/api/received-invoice/{id}", dto);
    }

    /// <summary>
    /// Approves a received invoice for payment.
    /// </summary>
    public async Task<ReceivedInvoiceDto?> ApproveAsync(long id)
    {
        return await PostWithoutBodyAsync<ReceivedInvoiceDto>($"/api/received-invoice/{id}/approve");
    }

    /// <summary>
    /// Marks a received invoice as paid.
    /// </summary>
    public async Task<ReceivedInvoiceDto?> MarkAsPaidAsync(long id)
    {
        return await PostWithoutBodyAsync<ReceivedInvoiceDto>($"/api/received-invoice/{id}/mark-paid");
    }

    /// <summary>
    /// Rejects a received invoice.
    /// </summary>
    public async Task<ReceivedInvoiceDto?> RejectAsync(long id)
    {
        return await PostWithoutBodyAsync<ReceivedInvoiceDto>($"/api/received-invoice/{id}/reject");
    }

    /// <summary>
    /// Soft-deletes a received invoice.
    /// </summary>
    public async Task<bool> DeleteAsync(long id)
    {
        return await DeleteAsync($"/api/received-invoice/{id}");
    }
}
