namespace Fakvio.Contracts.Dto.Auth;

/// <summary>
/// Response from the email verification endpoint.
/// Contains both the verification status and the tenant provisioning result.
///
/// WHY: Previously, VerifyEmailAsync returned a simple bool and silently swallowed
/// provisioning errors. This DTO surfaces the provisioning outcome so that:
/// - The API can return a meaningful message to the user
/// - The UI can show a warning if provisioning failed (email is verified but tenant is not ready)
/// - SysAdmin can see which step failed without digging through server logs
/// </summary>
public class VerifyEmailResponse
{
    /// <summary>
    /// True if the email was successfully verified (token was valid and not expired).
    /// This is independent of provisioning — email can be verified even if provisioning fails.
    /// </summary>
    public bool EmailVerified { get; set; }

    /// <summary>
    /// True if the tenant schema was successfully provisioned after email verification.
    /// False if provisioning failed or was skipped (e.g., user has no CompanyId).
    /// </summary>
    public bool TenantProvisioned { get; set; }

    /// <summary>
    /// Human-readable message about the overall result.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// If provisioning failed, contains the error details (step + exception message).
    /// Null when provisioning succeeded or was not attempted.
    /// </summary>
    public string? ProvisioningError { get; set; }
}
