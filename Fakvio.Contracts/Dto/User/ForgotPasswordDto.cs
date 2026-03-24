using System.ComponentModel.DataAnnotations;

namespace Fakvio.Contracts.Dto.User;

/// <summary>
/// DTO for the "Forgot Password" request.
/// Contains only the email address — the server generates a reset token
/// and sends an email with a link to set a new password.
/// </summary>
public class ForgotPasswordDto
{
    /// <summary>
    /// Email address of the user requesting a password reset.
    /// Must match an existing account. If not found, the server still returns success
    /// to prevent email enumeration attacks.
    /// </summary>
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;
}
