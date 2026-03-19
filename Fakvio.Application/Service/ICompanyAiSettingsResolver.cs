using Fakvio.Contracts.Dto.Chat;

namespace Fakvio.Application.Service;

/// <summary>
/// Resolves the effective AI provider for the current request.
///
/// Uses a 2-tier fallback chain (same pattern as EmailService SMTP resolution):
///   Tier 1: Company-specific AI settings from CompanySystemSettings (master DB).
///           Only used when the current request has a CompanyId AND that company
///           has configured AI settings (e.g., AiClaudeApiKey is non-empty).
///   Tier 2: System-wide AI settings from appsettings.json (IAiProviderFactory).
///           Used when company AI settings are not configured.
///
/// This allows each company to use its own AI API keys and preferred models,
/// while falling back to the global settings when company-specific config is absent.
///
/// Junior note: This is a SCOPED service — it resolves settings per HTTP request
/// based on the current tenant (CompanyId from JWT).
/// </summary>
public interface ICompanyAiSettingsResolver
{
    /// <summary>
    /// Resolves the AI provider to use for the current request.
    /// Checks company settings first, then falls back to the global provider factory.
    /// </summary>
    /// <param name="requestedProvider">
    /// Provider name explicitly requested by the user (from the chat UI dropdown).
    /// Null means "use the default provider" (company default → system default).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resolved IAiProvider instance ready for use.</returns>
    Task<IAiProvider> ResolveProviderAsync(string? requestedProvider, CancellationToken ct = default);

    /// <summary>
    /// Returns the list of available AI provider names for the current company.
    /// Includes company-specific providers (if configured) plus system-wide providers.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of provider names available for the current request.</returns>
    Task<IReadOnlyList<string>> GetAvailableProvidersAsync(CancellationToken ct = default);
}
