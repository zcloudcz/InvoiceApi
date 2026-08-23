namespace Fakvio.UI.Shared.Models;

/// <summary>
/// Blazor-side model for self-registration form.
/// Mirrors the API's RegisterRequest DTO.
/// Password is NOT included — user sets it after email verification.
/// </summary>
public class RegisterRequest
{
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;

    /// <summary>
    /// Czech registration number (IČO) — required for registration.
    /// </summary>
    public string RegistrationNumber { get; set; } = string.Empty;

    /// <summary>
    /// Registered office — street and building number.
    /// Pre-filled from ARES after the lookup; the user may correct it before submitting.
    /// </summary>
    public string? Street { get; set; }

    /// <summary>
    /// Registered office — city. See <see cref="Street"/>.
    /// </summary>
    public string? City { get; set; }

    /// <summary>
    /// Registered office — postal code. See <see cref="Street"/>.
    /// </summary>
    public string? PostalCode { get; set; }

    /// <summary>
    /// Registered office — country. See <see cref="Street"/>.
    /// </summary>
    public string? Country { get; set; }
}
