using InvoiceApi.Contracts.Dto.SystemConfiguration;

namespace InvoiceApi.Application.Service;

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
}
