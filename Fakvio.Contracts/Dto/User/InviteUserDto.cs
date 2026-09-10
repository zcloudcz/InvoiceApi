using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.User;

/// <summary>
/// DTO for inviting a new user via email.
/// The user will receive an email with a link to set their password.
/// No password is needed here — the user sets it themselves via the invitation link.
/// </summary>
public class InviteUserDto
{
    /// <summary>
    /// Email address of the user to invite.
    /// This will also be their login email.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// User's first name (displayed in the invitation email and UI)
    /// </summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>
    /// User's last name (displayed in the invitation email and UI)
    /// </summary>
    public string LastName { get; set; } = string.Empty;

    /// <summary>
    /// Role to assign to the invited user (User, Admin, SysAdmin)
    /// </summary>
    public EUserRole Role { get; set; } = EUserRole.User;

    /// <summary>
    /// Company ID to assign to the invited user.
    /// Required for non-SysAdmin roles.
    /// </summary>
    public long? CompanyId { get; set; }
}

/// <summary>
/// Result of inviting a user: the created user plus the raw invitation token.
///
/// Why the token lives here and not on <see cref="UserDto"/>:
/// <c>UserDto</c> is what every user-listing endpoint returns, and those endpoints are
/// open to any authenticated member of a company. <c>POST /api/user/set-password</c>
/// accepts an invitation token anonymously — the token IS the credential. A token on the
/// listing DTO therefore lets any colleague take over another account (issue #364).
/// Keeping it on a separate result type means only a caller that explicitly asked for the
/// invitation can ever see it.
/// </summary>
public class InvitedUserDto
{
    /// <summary>The user that was created by the invitation.</summary>
    public UserDto User { get; set; } = new();

    /// <summary>
    /// Raw invitation token used to build the set-password link.
    /// Server-side only — the API never puts this in an HTTP response body.
    /// </summary>
    public string InvitationToken { get; set; } = string.Empty;
}
