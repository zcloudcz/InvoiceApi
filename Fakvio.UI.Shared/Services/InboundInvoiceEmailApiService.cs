using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.InvoiceEmail;
using Fakvio.Domain.Enums;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor-side client for the inbound invoice email inbox API.
/// </summary>
public class InboundInvoiceEmailApiService : ApiClientBase
{
    public InboundInvoiceEmailApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<InboundInvoiceEmailApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    public async Task<PagedResult<InboundInvoiceEmailDto>> GetListAsync(
        int page = 1, int pageSize = 20,
        EInvoiceEmailStatus? status = null,
        EInvoiceDirection? direction = null,
        string? search = null)
    {
        var url = $"/api/inbound-invoice-email?page={page}&pageSize={pageSize}";
        if (status.HasValue) url += $"&status={(int)status.Value}";
        if (direction.HasValue) url += $"&direction={(int)direction.Value}";
        if (!string.IsNullOrWhiteSpace(search)) url += $"&search={Uri.EscapeDataString(search)}";

        try
        {
            return await GetAsync<PagedResult<InboundInvoiceEmailDto>>(url)
                   ?? new PagedResult<InboundInvoiceEmailDto>([], 0, page, pageSize);
        }
        catch (ApiException)
        {
            return new PagedResult<InboundInvoiceEmailDto>([], 0, page, pageSize);
        }
    }

    public async Task<InboundInvoiceEmailDto?> GetDetailAsync(long id)
    {
        try { return await GetAsync<InboundInvoiceEmailDto>($"/api/inbound-invoice-email/{id}"); }
        catch (ApiException) { return null; }
    }

    public async Task IgnoreAsync(long id)
    {
        try { await PostWithoutBodyAsync<object>($"/api/inbound-invoice-email/{id}/ignore"); }
        catch (ApiException) { }
    }

    public async Task RetryAsync(long id)
    {
        try { await PostWithoutBodyAsync<object>($"/api/inbound-invoice-email/{id}/retry"); }
        catch (ApiException) { }
    }
}
