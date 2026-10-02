using Fakvio.Contracts.Dto.Vies;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Client-side service for the VIES (EU VAT registry) verification endpoint.
/// Used by the "Ověřit ve VIES" button next to the DIČ field on the client form.
/// </summary>
public class ViesApiService : ApiClientBase
{
    public ViesApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<ViesApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Verifies a VAT ID against VIES. Never throws for an invalid/unavailable result — those
    /// are distinguished in <see cref="ViesVerificationResult.Status"/>, not by an exception.
    /// </summary>
    public async Task<ViesVerificationResult?> VerifyAsync(string vatId)
    {
        return await GetAsync<ViesVerificationResult>($"/api/vies/{Uri.EscapeDataString(vatId)}");
    }
}
