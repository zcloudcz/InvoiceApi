using Fakvio.Contracts.Dto.InvoiceEmail;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor-side client for the invoice mailbox API.
/// </summary>
public class InvoiceMailboxApiService : ApiClientBase
{
    public InvoiceMailboxApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<InvoiceMailboxApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    public async Task<InvoiceMailboxDto?> GetAsync()
    {
        try { return await GetAsync<InvoiceMailboxDto>("/api/invoice-mailbox"); }
        catch (ApiException) { return null; }
    }

    public async Task<InvoiceMailboxDto?> ActivateAsync()
        => await PostWithoutBodyAsync<InvoiceMailboxDto>("/api/invoice-mailbox/activate");

    public async Task<InvoiceMailboxDto?> DeactivateAsync()
        => await PostWithoutBodyAsync<InvoiceMailboxDto>("/api/invoice-mailbox/deactivate");

    public async Task<InvoiceMailboxDto?> RegenerateAsync()
        => await PostWithoutBodyAsync<InvoiceMailboxDto>("/api/invoice-mailbox/regenerate");
}
