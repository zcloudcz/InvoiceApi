using Fakvio.Contracts.Dto.RecognizedCounterparty;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor-side client for the recognized-counterparty registry API
/// (RecognizedCounterpartyController on the server).
/// Registry of known bank accounts (insurance, tax office, …) used to
/// recognize recurring payments without an invoice.
/// </summary>
public class RecognizedCounterpartyApiService : ApiClientBase
{
    public RecognizedCounterpartyApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<RecognizedCounterpartyApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>Lists all registry entries (including inactive), ordered by label.</summary>
    public Task<List<RecognizedCounterpartyDto>?> GetAllAsync() =>
        GetAsync<List<RecognizedCounterpartyDto>>("api/recognized-counterparties");

    /// <summary>Gets a single registry entry.</summary>
    public Task<RecognizedCounterpartyDto?> GetByIdAsync(long id) =>
        GetAsync<RecognizedCounterpartyDto>($"api/recognized-counterparties/{id}");

    /// <summary>
    /// Creates an entry. The response includes RecognizedCount — how many
    /// previously unmatched transactions the automatic re-scan recognized.
    /// </summary>
    public Task<SaveRecognizedCounterpartyResponse?> CreateAsync(SaveRecognizedCounterpartyRequest request) =>
        PostAsync<SaveRecognizedCounterpartyRequest, SaveRecognizedCounterpartyResponse>(
            "api/recognized-counterparties", request);

    /// <summary>Updates an entry (response includes RecognizedCount, see CreateAsync).</summary>
    public Task<SaveRecognizedCounterpartyResponse?> UpdateAsync(long id, SaveRecognizedCounterpartyRequest request) =>
        PutAsync<SaveRecognizedCounterpartyRequest, SaveRecognizedCounterpartyResponse>(
            $"api/recognized-counterparties/{id}", request);

    /// <summary>Deletes an entry; its recognized transactions revert to Unmatched.</summary>
    public Task<bool> DeleteAsync(long id) =>
        DeleteAsync($"api/recognized-counterparties/{id}");

    /// <summary>Manually re-runs recognition over unmatched transactions. Returns recognized count.</summary>
    public Task<int> RescanAsync() =>
        PostAsync<object, int>("api/recognized-counterparties/rescan", new { });
}
