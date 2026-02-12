namespace InvoiceApi.Application.Dto.Auth;

/// <summary>
/// DTO returned after a successful registration.
/// Contains the new user's ID and a message about email verification.
/// </summary>
public class RegisterResponse
{
    /// <summary>
    /// ID of the newly created user
    /// </summary>
    public long UserId { get; set; }

    /// <summary>
    /// Email address of the registered user
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable message (e.g., "Check your email for verification link")
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// True if the user must verify their email before logging in.
    /// Always true for self-registration; false for OAuth users (auto-verified).
    /// </summary>
    public bool RequiresEmailVerification { get; set; }
}
