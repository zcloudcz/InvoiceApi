namespace Fakvio.Contracts.Dto.User;

/// <summary>
/// DTO for setting a password via invitation token.
/// Used when a user clicks the invitation link and sets their password for the first time.
/// The token is validated server-side — no authentication is required for this operation.
/// </summary>
public class SetPasswordDto
{
    /// <summary>
    /// The invitation token from the email link.
    /// Must match a valid, non-expired token in the database.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// The new password the user wants to set.
    /// Will be hashed before storing in the database.
    /// </summary>
    public string NewPassword { get; set; } = string.Empty;
}
