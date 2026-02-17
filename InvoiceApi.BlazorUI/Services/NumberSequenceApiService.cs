using InvoiceApi.Contracts.Dto.NumberSequence;
using InvoiceApi.Domain.Enums;
using Microsoft.AspNetCore.Components.Authorization;

namespace InvoiceApi.BlazorUI.Services;

/// <summary>
/// API client for number sequence management.
/// Inherits ApiClientBase for shared auth/HTTP logic.
/// Consumes endpoints from NumberSequenceController.
/// </summary>
public class NumberSequenceApiService : ApiClientBase
{
    public NumberSequenceApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<NumberSequenceApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    // --- Sequence endpoints ---

    /// <summary>
    /// Gets all number sequences, optionally filtered by document type.
    /// GET /api/numbersequence
    /// </summary>
    public async Task<List<NumberSequenceDto>> GetAllSequencesAsync(
        EDocumentType? documentType = null, bool includeInactive = false)
    {
        var url = $"/api/numbersequence?includeInactive={includeInactive}";
        if (documentType.HasValue)
            url += $"&documentType={documentType.Value}";

        return await GetAsync<List<NumberSequenceDto>>(url) ?? new();
    }

    /// <summary>
    /// Gets a specific sequence by ID.
    /// GET /api/numbersequence/{id}
    /// </summary>
    public async Task<NumberSequenceDto?> GetSequenceByIdAsync(long id)
    {
        return await GetAsync<NumberSequenceDto>($"/api/numbersequence/{id}");
    }

    /// <summary>
    /// Creates a new number sequence.
    /// POST /api/numbersequence
    /// </summary>
    public async Task<NumberSequenceDto?> CreateSequenceAsync(CreateNumberSequenceDto dto)
    {
        return await PostAsync<CreateNumberSequenceDto, NumberSequenceDto>("/api/numbersequence", dto);
    }

    /// <summary>
    /// Sets a sequence as the default for its document type.
    /// POST /api/numbersequence/{id}/set-default
    /// </summary>
    public async Task<NumberSequenceDto?> SetAsDefaultAsync(long id)
    {
        return await PostAsync<object, NumberSequenceDto>($"/api/numbersequence/{id}/set-default", new { });
    }

    /// <summary>
    /// Deactivates (soft deletes) a sequence.
    /// DELETE /api/numbersequence/{id}
    /// </summary>
    public async Task<bool> DeactivateSequenceAsync(long id)
    {
        return await DeleteAsync($"/api/numbersequence/{id}");
    }

    /// <summary>
    /// Previews the next number that would be generated for a sequence.
    /// GET /api/numbersequence/{id}/preview
    /// </summary>
    public async Task<string?> PreviewNextNumberAsync(long id, DateTime? issueDate = null)
    {
        var url = $"/api/numbersequence/{id}/preview";
        if (issueDate.HasValue)
            url += $"?issueDate={issueDate.Value:yyyy-MM-dd}";

        var result = await GetAsync<PreviewResult>(url);
        return result?.NextNumber;
    }

    /// <summary>
    /// Updates an existing number sequence.
    /// PUT /api/numbersequence/{id}
    /// </summary>
    public async Task<NumberSequenceDto?> UpdateSequenceAsync(long id, UpdateNumberSequenceDto dto)
    {
        return await PutAsync<UpdateNumberSequenceDto, NumberSequenceDto>($"/api/numbersequence/{id}", dto);
    }

    /// <summary>
    /// Previews the next document number for a document type using the default sequence.
    /// GET /api/numbersequence/preview-by-type?documentType=...&issueDate=...
    /// Returns null if no default sequence is configured (not an error for UI purposes).
    /// </summary>
    public async Task<string?> PreviewByDocumentTypeAsync(EDocumentType documentType, DateTime? issueDate = null)
    {
        var url = $"/api/numbersequence/preview-by-type?documentType={documentType}";
        if (issueDate.HasValue)
            url += $"&issueDate={issueDate.Value:yyyy-MM-dd}";

        // GetAsync returns null on 404 (no default sequence) — this is expected
        var result = await GetAsync<PreviewResult>(url);
        return result?.NextNumber;
    }

    // --- Format endpoints ---

    /// <summary>
    /// Gets all number sequence formats.
    /// GET /api/numbersequence/formats
    /// </summary>
    public async Task<List<NumberSequenceFormatDto>> GetAllFormatsAsync(bool includeInactive = false)
    {
        return await GetAsync<List<NumberSequenceFormatDto>>(
            $"/api/numbersequence/formats?includeInactive={includeInactive}") ?? new();
    }

    /// <summary>
    /// Creates a new number sequence format.
    /// POST /api/numbersequence/formats
    /// </summary>
    public async Task<NumberSequenceFormatDto?> CreateFormatAsync(CreateNumberSequenceFormatDto dto)
    {
        return await PostAsync<CreateNumberSequenceFormatDto, NumberSequenceFormatDto>(
            "/api/numbersequence/formats", dto);
    }

    /// <summary>
    /// Updates an existing number sequence format.
    /// PUT /api/numbersequence/formats/{id}
    /// </summary>
    public async Task<NumberSequenceFormatDto?> UpdateFormatAsync(long id, UpdateNumberSequenceFormatDto dto)
    {
        return await PutAsync<UpdateNumberSequenceFormatDto, NumberSequenceFormatDto>(
            $"/api/numbersequence/formats/{id}", dto);
    }

    /// <summary>
    /// Helper DTO for deserializing the preview response.
    /// The API returns { sequenceId, issueDate, nextNumber }.
    /// </summary>
    private class PreviewResult
    {
        public long SequenceId { get; set; }
        public string NextNumber { get; set; } = string.Empty;
    }
}
