using System.ComponentModel.DataAnnotations;

namespace InvoiceApi.Application.Dto.Auth;

/// <summary>
/// DTO for email verification — contains the token from the verification link.
/// The token is a GUID generated during registration and sent via email.
/// </summary>
public class VerifyEmailRequest
{
    /// <summary>
    /// Email verification token (GUID string from the verification link)
    /// </summary>
    [Required]
    public string Token { get; set; } = string.Empty;
}
