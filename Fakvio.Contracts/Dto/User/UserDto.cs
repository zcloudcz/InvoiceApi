using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.User;

/// <summary>
/// DTO for user data
/// </summary>
public class UserDto
{
    public long Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public EUserRole Role { get; set; }
    public long? CompanyId { get; set; }
    public string? CompanyName { get; set; }
    public bool IsActive { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Whether the user has a pending invitation (hasn't set their password yet).
    /// Useful for admin UI to show invitation status.
    /// </summary>
    public bool IsInvitationPending { get; set; }

    /// <summary>
    /// The invitation token (only set when the user was just invited).
    /// Used internally to build the invitation link — not exposed to end users.
    /// </summary>
    public string? InvitationToken { get; set; }

    /// <summary>
    /// External OAuth provider (None = local password login).
    /// Useful for admin UI to show which provider the user signed up with.
    /// </summary>
    public EExternalProvider ExternalProvider { get; set; }

    /// <summary>
    /// Whether the user uses external OAuth login (computed from ExternalProvider).
    /// When true, the user cannot change their password or use password login.
    /// </summary>
    public bool IsExternalLogin { get; set; }

    /// <summary>
    /// Whether the user has verified their email address.
    /// Unverified users cannot log in. Useful for admin UI status display.
    /// </summary>
    public bool IsEmailVerified { get; set; }

    /// <summary>
    /// Whether Two-Factor Authentication is enabled for this user.
    /// Useful for admin UI to see 2FA status and allow force-disable for locked-out users.
    /// </summary>
    public bool TwoFactorEnabled { get; set; }

    /// <summary>
    /// The 2FA method chosen by the user (None, Totp, Email).
    /// </summary>
    public ETwoFactorMethod TwoFactorMethod { get; set; }
}

/// <summary>
/// DTO for creating a new user
/// </summary>
public class CreateUserDto
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public EUserRole Role { get; set; } = EUserRole.User;
    public long? CompanyId { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// DTO for updating user
/// </summary>
public class UpdateUserDto
{
    public string? Email { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public EUserRole? Role { get; set; }
    public long? CompanyId { get; set; }
    public bool? IsActive { get; set; }
}

/// <summary>
/// DTO for changing password (user changes their own password — requires current password)
/// </summary>
public class ChangePasswordDto
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>
/// DTO for admin password reset (Admin/SysAdmin resets a user's password without knowing the old one)
/// </summary>
public class AdminResetPasswordDto
{
    /// <summary>
    /// The new password to set for the user.
    /// The admin does not need to know the current password.
    /// </summary>
    public string NewPassword { get; set; } = string.Empty;
}
