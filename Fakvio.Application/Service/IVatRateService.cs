using Fakvio.Contracts.Dto.VatRate;

namespace Fakvio.Application.Service;

/// <summary>
/// Service for managing VAT rates (DPH sazby)
/// Handles business logic for VAT rate operations
/// </summary>
public interface IVatRateService
{
    /// <summary>
    /// Gets all VAT rates (optionally filtered by active status)
    /// </summary>
    /// <param name="includeInactive">Include inactive rates in results</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of VAT rates</returns>
    Task<List<VatRateDto>> GetAllVatRatesAsync(bool includeInactive = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific VAT rate by ID
    /// </summary>
    /// <param name="vatRateId">VAT rate ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>VAT rate data or null if not found</returns>
    Task<VatRateDto?> GetVatRateByIdAsync(long vatRateId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets active VAT rates valid at a specific date
    /// </summary>
    /// <param name="date">Date to check validity (defaults to current date)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of valid VAT rates for the given date</returns>
    Task<List<VatRateDto>> GetActiveVatRatesForDateAsync(DateTime? date = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the default standard VAT rate
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Default standard rate or null if not configured</returns>
    Task<VatRateDto?> GetDefaultStandardRateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the default reduced VAT rate
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Default reduced rate or null if not configured</returns>
    Task<VatRateDto?> GetDefaultReducedRateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new VAT rate
    /// Validates that only one default standard and one default reduced rate exists
    /// </summary>
    /// <param name="createDto">VAT rate creation data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created VAT rate</returns>
    /// <exception cref="InvalidOperationException">Thrown when validation rules are violated</exception>
    Task<VatRateDto> CreateVatRateAsync(CreateVatRateDto createDto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing VAT rate
    /// Validates that only one default standard and one default reduced rate exists
    /// </summary>
    /// <param name="vatRateId">VAT rate ID to update</param>
    /// <param name="updateDto">Updated data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated VAT rate or null if not found</returns>
    /// <exception cref="InvalidOperationException">Thrown when validation rules are violated</exception>
    Task<VatRateDto?> UpdateVatRateAsync(long vatRateId, UpdateVatRateDto updateDto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a VAT rate (soft delete - sets IsActive = false)
    /// Cannot delete rate if it's used in any invoices
    /// </summary>
    /// <param name="vatRateId">VAT rate ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted, false if not found or used in invoices</returns>
    Task<bool> DeleteVatRateAsync(long vatRateId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a VAT rate as default
    /// Automatically unsets the previous default rate of the same type (standard/reduced)
    /// </summary>
    /// <param name="vatRateId">VAT rate ID to set as default</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated VAT rate or null if not found</returns>
    Task<VatRateDto?> SetAsDefaultAsync(long vatRateId, CancellationToken cancellationToken = default);
}
