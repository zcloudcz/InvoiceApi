using System.ComponentModel.DataAnnotations;

namespace Fakvio.Contracts.Dto.Auth;

/// <summary>
/// DTO for self-registration of a new company and admin user.
/// Password is NOT set during registration — the user receives an email
/// with a link to set their password (which also verifies the email).
/// IČO (registration number) is required for ARES auto-fill of company data.
/// </summary>
public class RegisterRequest
{
    /// <summary>
    /// User's email address — used as the login identifier.
    /// Must be unique across the system.
    /// </summary>
    [Required, EmailAddress, MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// User's first name
    /// </summary>
    [Required, MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    /// <summary>
    /// User's last name
    /// </summary>
    [Required, MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    /// <summary>
    /// Name of the company being registered.
    /// A new Client (IsIssuer = true) record will be created with this name.
    /// </summary>
    [Required, MaxLength(200)]
    public string CompanyName { get; set; } = string.Empty;

    /// <summary>
    /// Czech registration number (IČO) — required.
    /// The system calls ARES to fetch and auto-fill company details
    /// (tax number, address, etc.)
    /// </summary>
    [Required, MaxLength(20)]
    public string RegistrationNumber { get; set; } = string.Empty;
}
