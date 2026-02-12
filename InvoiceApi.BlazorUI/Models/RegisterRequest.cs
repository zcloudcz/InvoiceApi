namespace InvoiceApi.BlazorUI.Models;

/// <summary>
/// Blazor-side model for self-registration form.
/// Mirrors the API's RegisterRequest DTO.
/// </summary>
public class RegisterRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string? RegistrationNumber { get; set; }
}
