using Fakvio.Domain.Common;
using Fakvio.Domain.Enums;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Represents a user in the system
/// Users can have different roles and belong to a company
/// </summary>
public class User : BaseEntity
{
    /// <summary>
    /// User's email address (used as login)
    /// Must be unique across the system
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Hashed password (BCrypt).
    /// Nullable because OAuth users authenticate via external providers and don't have a local password.
    /// Never store plain text passwords!
    /// </summary>
    public string? PasswordHash { get; set; }

    /// <summary>
    /// User's first name
    /// </summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>
    /// User's last name
    /// </summary>
    public string LastName { get; set; } = string.Empty;

    /// <summary>
    /// User's role in the system
    /// Determines access rights and permissions
    /// </summary>
    public EUserRole Role { get; set; } = EUserRole.User;

    /// <summary>
    /// Company (Issuer) this user belongs to
    /// Null for SysAdmin users who can access all companies
    /// Required for User and Admin roles
    /// </summary>
    public long? CompanyId { get; set; }

    /// <summary>
    /// Navigation property to Company
    /// The company this user works for
    /// </summary>
    public Client? Company { get; set; }

    /// <summary>
    /// Is this user account active?
    /// Inactive users cannot log in
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Last successful login date
    /// Used for security auditing and user activity tracking
    /// </summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// Unique invitation token (GUID) sent to the user via email.
    /// Used for the password setup flow when a user is invited.
    /// Null when no invitation is pending.
    /// </summary>
    public string? InvitationToken { get; set; }

    /// <summary>
    /// Expiration date for the invitation token.
    /// After this date, the token is no longer valid and the user must be re-invited.
    /// Typically set to 48 hours after invitation.
    /// </summary>
    public DateTime? InvitationTokenExpiresAt { get; set; }

    /// <summary>
    /// Indicates whether the user has a pending invitation (hasn't set their password yet).
    /// When true, the user cannot log in until they complete the password setup.
    /// </summary>
    public bool IsInvitationPending { get; set; } = false;

    /// <summary>
    /// External OAuth provider used for authentication.
    /// None = standard email + password login.
    /// Other values indicate the user signed up via an external provider (Google, Microsoft, etc.)
    /// </summary>
    public EExternalProvider ExternalProvider { get; set; } = EExternalProvider.None;

    /// <summary>
    /// Provider-specific user identifier (e.g., Google "sub" claim, Facebook user ID).
    /// Null for local password users. Together with ExternalProvider, uniquely identifies
    /// an OAuth user across the system.
    /// </summary>
    public string? ExternalProviderId { get; set; }

    /// <summary>
    /// Whether the user has verified their email address.
    /// Self-registered users must verify via email link before they can log in.
    /// Invited users and OAuth users are auto-verified (set to true on creation).
    /// </summary>
    public bool IsEmailVerified { get; set; } = false;

    /// <summary>
    /// Email verification token (GUID string) sent to the user's email.
    /// Similar to InvitationToken — used for the email verification flow.
    /// Cleared after successful verification.
    /// </summary>
    public string? EmailVerificationToken { get; set; }

    /// <summary>
    /// Expiration timestamp for the email verification token (typically 24 hours).
    /// After this time, the user must request a new verification email.
    /// </summary>
    public DateTime? EmailVerificationTokenExpiresAt { get; set; }

    /// <summary>
    /// Computed property: true if this user authenticates via an external OAuth provider.
    /// OAuth users don't have a local password and cannot use the password login flow.
    /// </summary>
    public bool IsExternalLogin => ExternalProvider != EExternalProvider.None;

    // ─── Two-Factor Authentication ─────────────────────────────────────────

    /// <summary>
    /// Whether Two-Factor Authentication is enabled for this user.
    /// When true, login requires a second verification step after the password.
    /// </summary>
    public bool TwoFactorEnabled { get; set; } = false;

    /// <summary>
    /// The 2FA method chosen by the user: None (disabled), Totp (authenticator app), or Email (OTP code).
    /// </summary>
    public ETwoFactorMethod TwoFactorMethod { get; set; } = ETwoFactorMethod.None;

    /// <summary>
    /// TOTP shared secret encrypted with ASP.NET Core Data Protection API.
    /// Only set when TwoFactorMethod == Totp. Never store the raw base32 secret!
    /// </summary>
    public string? TotpSecretEncrypted { get; set; }

    /// <summary>
    /// Timestamp when 2FA was enabled. Null if 2FA has never been enabled.
    /// </summary>
    public DateTime? TwoFactorEnabledAt { get; set; }

    /// <summary>
    /// BCrypt-hashed 6-digit email OTP code. Only set during email-based 2FA login flow.
    /// Cleared after successful verification or expiration.
    /// </summary>
    public string? TwoFactorEmailCode { get; set; }

    /// <summary>
    /// Expiration time for the email OTP code (typically 5 minutes after generation).
    /// </summary>
    public DateTime? TwoFactorEmailCodeExpiresAt { get; set; }

    /// <summary>
    /// Encrypted temporary session token issued after successful password verification.
    /// Used to link the second step (code verification) to the first step (password).
    /// Format: "userId|timestamp" encrypted via Data Protection API.
    /// </summary>
    public string? TwoFactorSessionToken { get; set; }

    /// <summary>
    /// Expiration time for the 2FA session token (typically 5 minutes).
    /// </summary>
    public DateTime? TwoFactorSessionTokenExpiresAt { get; set; }

    /// <summary>
    /// Number of consecutive failed 2FA code attempts in the current session.
    /// After 5 failures, the session token is invalidated (rate limiting).
    /// </summary>
    public int FailedTwoFactorAttempts { get; set; } = 0;

    /// <summary>
    /// Full name of the user (computed property)
    /// </summary>
    public string FullName => $"{FirstName} {LastName}";
}
