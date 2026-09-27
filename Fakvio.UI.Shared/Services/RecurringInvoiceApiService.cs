using Fakvio.Contracts.Dto.RecurringInvoice;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// API client for recurring invoice schedules. Consumes endpoints from
/// RecurringInvoiceController (DEVGUIDE §4.13).
/// </summary>
public class RecurringInvoiceApiService : ApiClientBase
{
    public RecurringInvoiceApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<RecurringInvoiceApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Gets all schedules for a template — used by the "Recurring schedule" panel on the
    /// invoice template detail page.
    /// GET /api/recurringinvoice?templateId=
    /// </summary>
    public async Task<List<RecurringInvoiceScheduleDto>> GetByTemplateAsync(long templateId)
    {
        try
        {
            return await GetAsync<List<RecurringInvoiceScheduleDto>>($"/api/recurringinvoice?templateId={templateId}")
                   ?? [];
        }
        catch (ApiException)
        {
            // Graceful degradation for a list endpoint — show an empty panel instead of crashing
            // the whole template detail page. 401 is already handled by UnauthorizedRedirectHandler.
            return [];
        }
    }

    /// <summary>Creates a new schedule. Throws ApiException with the server's message on failure.</summary>
    public async Task<RecurringInvoiceScheduleDto?> CreateAsync(CreateRecurringInvoiceScheduleDto dto)
    {
        return await PostAsync<CreateRecurringInvoiceScheduleDto, RecurringInvoiceScheduleDto>("/api/recurringinvoice", dto);
    }

    /// <summary>Updates a schedule. Throws ApiException with the server's message on failure.</summary>
    public async Task<RecurringInvoiceScheduleDto?> UpdateAsync(long id, UpdateRecurringInvoiceScheduleDto dto)
    {
        return await PutAsync<UpdateRecurringInvoiceScheduleDto, RecurringInvoiceScheduleDto>($"/api/recurringinvoice/{id}", dto);
    }

    /// <summary>Pauses a schedule.</summary>
    public async Task<RecurringInvoiceScheduleDto?> PauseAsync(long id)
    {
        return await PostWithoutBodyAsync<RecurringInvoiceScheduleDto>($"/api/recurringinvoice/{id}/pause");
    }

    /// <summary>Resumes a paused schedule.</summary>
    public async Task<RecurringInvoiceScheduleDto?> ResumeAsync(long id)
    {
        return await PostWithoutBodyAsync<RecurringInvoiceScheduleDto>($"/api/recurringinvoice/{id}/resume");
    }

    /// <summary>Deletes (or deactivates, if it has fired before) a schedule.</summary>
    public async Task<bool> DeleteAsync(long id)
    {
        return await DeleteAsync($"/api/recurringinvoice/{id}");
    }
}
