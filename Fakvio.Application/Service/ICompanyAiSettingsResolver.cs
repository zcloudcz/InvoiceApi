using Fakvio.Contracts.Dto.Chat;

namespace Fakvio.Application.Service;

/// <summary>
/// Resolves the effective AI provider for a given company.
///
/// Uses a 2-tier fallback chain (same pattern as EmailService SMTP resolution):
///   Tier 1: Company-specific AI settings from CompanySystemSettings (master DB).
///           Only used when companyId is provided AND that company has configured
///           AI settings (e.g., AiClaudeApiKey is non-empty).
///   Tier 2: System-wide AI settings from appsettings.json (IAiProviderFactory).
///           Used when company AI settings are not configured.
///
/// CompanyId is passed explicitly as a parameter — the resolver does NOT read
/// from IHttpContextAccessor/ITenantResolver. This avoids issues in Azure Functions
/// where IHttpContextAccessor may not return the correct HttpContext.
///
/// Junior note: This is a SCOPED service — one instance per HTTP request.
/// </summary>
public interface ICompanyAiSettingsResolver
{
    /// <summary>
    /// Resolves the AI provider to use for a given company.
    /// Checks company settings first, then falls back to the global provider factory.
    /// </summary>
    /// <param name="companyId">
    /// The company ID to resolve settings for. Pass null for system-default only.
    /// </param>
    /// <param name="requestedProvider">
    /// Provider name explicitly requested by the user (from the chat UI dropdown).
    /// Null means "use the default provider" (company default → system default).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resolved IAiProvider instance ready for use.</returns>
    Task<IAiProvider> ResolveProviderAsync(long? companyId, string? requestedProvider, CancellationToken ct = default);

    /// <summary>
    /// Returns the list of available AI provider names for a given company.
    /// Includes company-specific providers (if configured) plus system-wide providers.
    /// </summary>
    /// <param name="companyId">The company ID. Pass null for system-wide providers only.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of provider names available.</returns>
    Task<IReadOnlyList<string>> GetAvailableProvidersAsync(long? companyId, CancellationToken ct = default);
}
