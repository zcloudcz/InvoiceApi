namespace Fakvio.UI.Shared.Models;

/// <summary>
/// Blazor-side model for registration response from the API.
/// </summary>
public class RegisterResponse
{
    public long UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool RequiresEmailVerification { get; set; }
}
