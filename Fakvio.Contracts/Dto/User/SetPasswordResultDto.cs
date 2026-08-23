namespace Fakvio.Contracts.Dto.User;

/// <summary>
/// Result of the "set password via invitation token" flow.
///
/// Why a DTO instead of a plain bool: setting the password also triggers tenant
/// provisioning (creating the PostgreSQL schema for the company). Provisioning may
/// fail without invalidating the password itself. The caller therefore needs two
/// independent pieces of information — otherwise the UI would report a plain
/// "Done, log in" while the workspace is unusable.
/// </summary>
public class SetPasswordResultDto
{
    /// <summary>
    /// Was the password actually set?
    /// False means the invitation token was invalid or expired — nothing was changed.
    /// Named like <c>VerifyEmailResponse.EmailVerified</c> — the same two-outcome shape.
    /// </summary>
    public bool PasswordSet { get; set; }

    /// <summary>
    /// Is the user's workspace (tenant database schema) ready to be used?
    ///
    /// True  = provisioned, the user can log in and work.
    /// False = provisioning has not finished successfully — the password IS set,
    ///         but logging in would hit an unprovisioned tenant. The UI must say so
    ///         instead of reporting plain success.
    ///
    /// Always true for users without a company (SysAdmin), because such users have
    /// no tenant schema at all.
    /// </summary>
    public bool WorkspaceReady { get; set; }
}
