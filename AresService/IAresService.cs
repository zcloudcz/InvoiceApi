using AresService.Model;

namespace AresService;

/// <summary>
/// Service for fetching company information from ARES registry
/// ARES (Administrative Register of Economic Subjects) is Czech business registry
/// </summary>
public interface IAresService
{
    /// <summary>
    /// Fetches company information from ARES by registration number (IČO)
    /// Results are cached to avoid repeated API calls
    /// </summary>
    /// <param name="registrationNumber">Company registration number (IČO), 8 digits</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Company information if found, or error details if not found</returns>
    Task<AresCompanyInfo> GetCompanyInfoAsync(string registrationNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if cached data exists and is still valid (not expired)
    /// </summary>
    /// <param name="registrationNumber">Company registration number (IČO)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if valid cache exists, false otherwise</returns>
    Task<bool> IsCachedAsync(string registrationNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forces refresh of company data from ARES, bypassing cache
    /// Use this when you need up-to-date information
    /// </summary>
    /// <param name="registrationNumber">Company registration number (IČO)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Fresh company information from ARES</returns>
    Task<AresCompanyInfo> RefreshCompanyInfoAsync(string registrationNumber, CancellationToken cancellationToken = default);
}
