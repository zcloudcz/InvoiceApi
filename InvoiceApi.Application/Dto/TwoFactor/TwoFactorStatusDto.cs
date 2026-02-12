using InvoiceApi.Domain.Enums;

namespace InvoiceApi.Application.Dto.TwoFactor;

/// <summary>
/// DTO for returning the current 2FA status of a user.
/// Used by the TwoFactorSettings page to display whether 2FA is enabled
/// and which method the user has configured.
/// </summary>
public class TwoFactorStatusDto
{
    /// <summary>
    /// Whether 2FA is currently enabled for this user.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The active 2FA method (None, Totp, or Email).
    /// </summary>
    public ETwoFactorMethod Method { get; set; }

    /// <summary>
    /// When 2FA was enabled. Null if never enabled.
    /// </summary>
    public DateTime? EnabledAt { get; set; }
}
