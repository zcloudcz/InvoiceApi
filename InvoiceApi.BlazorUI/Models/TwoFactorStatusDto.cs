namespace InvoiceApi.BlazorUI.Models;

/// <summary>
/// Blazor model for 2FA status response from the API.
/// Mirrors the Contracts.Dto.TwoFactor.TwoFactorStatusDto but uses int for the method
/// (JSON deserialization from API returns integers for enums).
/// </summary>
public class TwoFactorStatusDto
{
    /// <summary>
    /// Whether 2FA is currently enabled for this user.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The active 2FA method as an integer: 0 = None, 1 = TOTP, 2 = Email.
    /// </summary>
    public int Method { get; set; }

    /// <summary>
    /// When 2FA was enabled. Null if never enabled.
    /// </summary>
    public DateTime? EnabledAt { get; set; }
}
