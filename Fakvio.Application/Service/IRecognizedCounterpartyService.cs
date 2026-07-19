using Fakvio.Contracts.Dto.RecognizedCounterparty;

namespace Fakvio.Application.Service;

/// <summary>
/// CRUD for the recognized-counterparty registry (known bank accounts of
/// insurance companies, tax office, … used to recognize recurring payments).
/// Tenant-scoped — runs against the current TenantDbContext.
///
/// Create/Update of an active entry automatically re-scans Unmatched
/// transactions so the new rule takes effect immediately; the returned
/// RecognizedCount feeds the UI snackbar.
/// </summary>
public interface IRecognizedCounterpartyService
{
    /// <summary>Returns all registry entries (including inactive), ordered by Label.</summary>
    Task<List<RecognizedCounterpartyDto>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Returns one entry or null when not found.</summary>
    Task<RecognizedCounterpartyDto?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Creates an entry. When active, re-scans Unmatched transactions and
    /// reports how many were recognized.
    /// </summary>
    Task<SaveRecognizedCounterpartyResponse> CreateAsync(
        SaveRecognizedCounterpartyRequest request, CancellationToken ct = default);

    /// <summary>
    /// Updates an entry. Returns null when not found. When active after the
    /// update, re-scans Unmatched transactions.
    /// </summary>
    Task<SaveRecognizedCounterpartyResponse?> UpdateAsync(
        long id, SaveRecognizedCounterpartyRequest request, CancellationToken ct = default);

    /// <summary>
    /// Hard-deletes an entry. Transactions recognized by it are reset to
    /// Unmatched (categorization is derived data — safe to recompute).
    /// Returns false when the entry does not exist.
    /// </summary>
    Task<bool> DeleteAsync(long id, CancellationToken ct = default);
}
