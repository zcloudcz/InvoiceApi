using System.ComponentModel.DataAnnotations;

namespace InvoiceApi.Application.Dto.Auth;

/// <summary>
/// DTO for self-registration of a new company and admin user.
/// The user provides their personal info, a password, and company details.
/// Optionally includes IČO (registration number) for ARES auto-fill of company data.
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
    /// Password for the new account.
    /// Minimum 6 characters, will be hashed with BCrypt before storage.
    /// Not required for OAuth registrations (handled server-side).
    /// </summary>
    [Required, MinLength(6), MaxLength(100)]
    public string Password { get; set; } = string.Empty;

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
    /// Optional Czech registration number (IČO).
    /// If provided, the system calls ARES to fetch and auto-fill company details
    /// (tax number, address, etc.)
    /// </summary>
    [MaxLength(20)]
    public string? RegistrationNumber { get; set; }
}
