using System.ComponentModel.DataAnnotations;

namespace Fakvio.UI.Shared.Models;

/// <summary>
/// Model for login request
/// </summary>
public class LoginRequest
{
    [Required(ErrorMessage = "Email je povinný")]
    [EmailAddress(ErrorMessage = "Neplatný formát emailu")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Heslo je povinné")]
    public string Password { get; set; } = string.Empty;
}
