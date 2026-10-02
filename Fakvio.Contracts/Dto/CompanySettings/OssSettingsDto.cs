namespace Fakvio.Contracts.Dto.CompanySettings;

/// <summary>
/// EU OSS registration of the current company (DEVGUIDE §4.15). Used by the tenant-scoped
/// <c>api/company-settings/oss</c> endpoint, both for reading and for saving.
/// </summary>
public class OssSettingsDto
{
    /// <summary>Is the company registered for the EU OSS scheme?</summary>
    public bool OssRegistered { get; set; }

    /// <summary>Date from which the registration is effective; ignored (cleared) when not registered.</summary>
    public DateTime? OssRegisteredSince { get; set; }
}
