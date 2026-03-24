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
}
