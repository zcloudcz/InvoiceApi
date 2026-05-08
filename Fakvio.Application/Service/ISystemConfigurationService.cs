using Fakvio.Contracts.Dto.SystemConfiguration;

namespace Fakvio.Application.Service;

/// <summary>
/// Service for reading and updating the global system configuration (SMTP, JWT settings).
/// The system configuration is a single-row table in the master database.
/// Only SysAdmin should be able to access these methods.
/// </summary>
public interface ISystemConfigurationService
{
    /// <summary>
    /// Gets the current system configuration.
    /// If no configuration exists yet (fresh install), creates a default one automatically.
    /// </summary>
    Task<SystemConfigurationDto> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// Updates the system configuration with the provided values.
    /// Creates a default record first if none exists.
    /// </summary>
    Task<SystemConfigurationDto> UpdateAsync(UpdateSystemConfigurationDto dto, CancellationToken ct = default);

    /// <summary>
    /// Gets the decrypted SMTP password for internal service use (EmailService).
    /// This is NOT exposed via API — the password never leaves the server.
    /// Returns null if no password is configured.
    /// </summary>
    Task<string?> GetSmtpPasswordAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets decrypted system-wide AI settings for internal service use (CompanyAiSettingsResolver).
    /// API keys are decrypted on-demand. NOT exposed via API.
    /// Returns null values for unconfigured providers.
    /// </summary>
    Task<SystemAiSettingsInternal> GetAiSettingsAsync(CancellationToken ct = default);

    /// <summary>
    /// Tests the Azure Blob Storage connection using the currently configured connection string.
    /// Attempts a lightweight operation (listing containers) to verify connectivity.
    /// Returns a result object with Success flag and an optional error message.
    /// </summary>
    Task<BlobTestConnectionResult> TestAzureBlobConnectionAsync(CancellationToken ct = default);
}

/// <summary>
/// Result of an Azure Blob Storage connection test.
/// Carries a success flag and, on failure, a human-readable error message
/// so the UI can show what went wrong without exposing sensitive internals.
/// </summary>
public class BlobTestConnectionResult
{
    /// <summary>True if the connection attempt succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>
    /// Short error description when Success = false.
    /// Null when Success = true.
    /// </summary>
    public string? Error { get; init; }
}

/// <summary>
/// Internal DTO for decrypted AI settings — used only by CompanyAiSettingsResolver.
/// Never exposed via API (API uses SystemConfigurationDto with HasXxxApiKey flags).
/// </summary>
public class SystemAiSettingsInternal
{
    public string? DefaultProvider { get; set; }
    public string? ClaudeApiKey { get; set; }
    public string? ClaudeModel { get; set; }
    public string? OpenAiApiKey { get; set; }
    public string? OpenAiModel { get; set; }
    public string? GeminiApiKey { get; set; }
    public string? GeminiModel { get; set; }
    public string? OllamaBaseUrl { get; set; }
    public string? OllamaModel { get; set; }
}
