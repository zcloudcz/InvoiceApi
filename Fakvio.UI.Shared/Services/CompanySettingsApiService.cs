using Fakvio.Contracts.Dto.CompanySettings;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor service for communicating with the CompanySystemSettings API endpoints.
/// Provides CRUD for tenant configurations and lifecycle actions (provision, activate, deactivate, migrate).
/// Only used by SysAdmin pages — all endpoints require SysAdmin role.
/// Inherits ApiClientBase for shared auth, logging, impersonation, and error handling.
/// </summary>
public class CompanySettingsApiService : ApiClientBase
{
    public CompanySettingsApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<CompanySettingsApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    #region Settings CRUD

    /// <summary>
    /// Gets all CompanySystemSettings records from the master database.
    /// Returns a list of all tenant configurations for the SysAdmin dashboard.
    /// </summary>
    public async Task<List<CompanySystemSettingsDto>> GetAllSettingsAsync()
    {
        try
        {
            return await GetAsync<List<CompanySystemSettingsDto>>("/api/company/settings") ?? [];
        }
        catch (ApiException)
        {
            // Graceful degradation for list endpoints — show empty grid instead of crashing.
            // 401 is already handled by UnauthorizedRedirectHandler (redirects to /login).
            return [];
        }
    }

    /// <summary>
    /// Gets the CompanySystemSettings for a specific company by its ID.
    /// Returns null if no settings exist for this company yet.
    /// </summary>
    /// <param name="companyId">Company (issuer) ID</param>
    public async Task<CompanySystemSettingsDto?> GetSettingsAsync(long companyId)
    {
        return await GetAsync<CompanySystemSettingsDto>($"/api/company/{companyId}/settings");
    }

    /// <summary>
    /// Creates a new CompanySystemSettings record.
    /// This registers the tenant configuration but does NOT provision the database.
    /// Call ProvisionTenantAsync separately to create the actual database.
    /// </summary>
    /// <param name="dto">Settings creation data (companyId, databaseName, maxUsers, notes)</param>
    public async Task<CompanySystemSettingsDto?> CreateSettingsAsync(CreateCompanySystemSettingsDto dto)
    {
        return await PostAsync<CreateCompanySystemSettingsDto, CompanySystemSettingsDto>(
            "/api/company/settings", dto);
    }

    /// <summary>
    /// Updates an existing CompanySystemSettings record.
    /// Only mutable fields can be changed (connectionString, maxUsers, adminNotes).
    /// Database name and provisioning status are immutable.
    /// </summary>
    /// <param name="companyId">Company (issuer) ID</param>
    /// <param name="dto">Updated settings data</param>
    public async Task<CompanySystemSettingsDto?> UpdateSettingsAsync(
        long companyId, UpdateCompanySystemSettingsDto dto)
    {
        return await PutAsync<UpdateCompanySystemSettingsDto, CompanySystemSettingsDto>(
            $"/api/company/{companyId}/settings", dto);
    }

    /// <summary>
    /// Gets the CompanySystemSettings for a specific company by its company ID.
    /// Returns null if no settings exist for this company yet.
    /// Alias for GetSettingsAsync — both query by company ID via GET /api/company/{companyId}/settings.
    /// </summary>
    /// <param name="companyId">Company (issuer) ID</param>
    public async Task<CompanySystemSettingsDto?> GetByCompanyIdAsync(long companyId)
    {
        return await GetAsync<CompanySystemSettingsDto>($"/api/company/{companyId}/settings");
    }

    /// <summary>
    /// Sends a test email using the company's SMTP settings to verify configuration.
    /// The test email is sent to the currently logged-in user's email address.
    /// Returns true if the test email was sent successfully, false if it failed.
    /// </summary>
    /// <param name="companyId">Company (issuer) ID whose SMTP to test</param>
    public async Task<bool> TestSmtpAsync(long companyId)
    {
        return await PostWithoutBodyBoolAsync($"/api/company/{companyId}/test-smtp");
    }

    #endregion

    #region Tenant Lifecycle

    /// <summary>
    /// Provisions a new tenant database for the given company.
    /// This is a long-running operation: CREATE DATABASE → migrations → seed code tables → create issuer.
    /// Prerequisites: CompanySystemSettings must already exist (via CreateSettingsAsync).
    /// </summary>
    /// <param name="companyId">Company (issuer) ID to provision</param>
    /// <returns>True if provisioning succeeded</returns>
    public async Task<bool> ProvisionTenantAsync(long companyId)
    {
        return await PostWithoutBodyBoolAsync($"/api/company/{companyId}/provision");
    }

    /// <summary>
    /// Activates a previously deactivated tenant.
    /// The tenant database must already be provisioned — this only flips the IsActive flag.
    /// After activation, users of this company can access tenant-scoped endpoints again.
    /// </summary>
    /// <param name="companyId">Company (issuer) ID to activate</param>
    /// <returns>True if activation succeeded</returns>
    public async Task<bool> ActivateTenantAsync(long companyId)
    {
        return await PutWithoutBodyBoolAsync($"/api/company/{companyId}/activate");
    }

    /// <summary>
    /// Deactivates a tenant. The database remains but all access is blocked.
    /// Users of this company will receive a 403 from TenantContextMiddleware.
    /// Does NOT delete the database — that's a manual DBA operation (safety measure).
    /// </summary>
    /// <param name="companyId">Company (issuer) ID to deactivate</param>
    /// <returns>True if deactivation succeeded</returns>
    public async Task<bool> DeactivateTenantAsync(long companyId)
    {
        return await PutWithoutBodyBoolAsync($"/api/company/{companyId}/deactivate");
    }

    /// <summary>
    /// Applies pending EF Core migrations to a specific tenant's database.
    /// Used for manual maintenance — startup auto-migration handles this automatically.
    /// </summary>
    /// <param name="companyId">Company (issuer) ID whose tenant DB to migrate</param>
    /// <returns>True if migration succeeded</returns>
    public async Task<bool> MigrateTenantAsync(long companyId)
    {
        return await PostWithoutBodyBoolAsync($"/api/company/{companyId}/migrate");
    }

    #endregion
}
