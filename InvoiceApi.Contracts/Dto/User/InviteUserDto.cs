using InvoiceApi.Domain.Enums;

namespace InvoiceApi.Contracts.Dto.User;

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
