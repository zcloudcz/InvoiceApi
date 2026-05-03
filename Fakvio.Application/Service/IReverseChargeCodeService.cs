using Fakvio.Contracts.Dto.ReverseChargeCode;

namespace Fakvio.Application.Service;

/// <summary>
/// Service for reading reverse charge codes (kódy předmětu plnění PDP).
///
/// Reverse charge codes are reference data from MFČR (Ministry of Finance) that map
/// each type of supply subject to the Czech reverse charge mechanism (§92a–§92e ZDPH)
/// to a numeric code used in the VAT control statement (kontrolní hlášení / EPO XML).
///
/// This service is intentionally READ-ONLY — admin CRUD is a separate task.
/// </summary>
public interface IReverseChargeCodeService
{
    /// <summary>
    /// Returns all codes that are currently active and within their validity window.
    /// A code is considered valid when:
    ///   - IsActive == true
    ///   - ValidFrom &lt;= today
    ///   - ValidTo == null OR ValidTo &gt;= today
    ///
    /// Ordered by Code ascending for a predictable UI list.
    /// </summary>
    /// <param name="cancellationToken">Propagates cancellation from the caller.</param>
    /// <returns>List of active, currently valid reverse charge codes.</returns>
    Task<List<ReverseChargeCodeDto>> GetAllActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up a reverse charge code by its MFČR code string (e.g., "5", "1a").
    /// Returns null if the code does not exist in the database.
    /// </summary>
    /// <param name="code">The MFČR code string. Case-sensitive (codes are numeric/alphanumeric).</param>
    /// <param name="cancellationToken">Propagates cancellation from the caller.</param>
    /// <returns>The matching code DTO, or null if not found.</returns>
    Task<ReverseChargeCodeDto?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up a reverse charge code by its database primary key (Id).
    /// Returns null if no record with the given Id exists.
    /// </summary>
    /// <param name="id">Database primary key (BaseEntity.Id).</param>
    /// <param name="cancellationToken">Propagates cancellation from the caller.</param>
    /// <returns>The matching code DTO, or null if not found.</returns>
    Task<ReverseChargeCodeDto?> GetByIdAsync(long id, CancellationToken cancellationToken = default);
}
