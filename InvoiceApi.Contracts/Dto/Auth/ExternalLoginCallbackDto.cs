using InvoiceApi.Domain.Enums;

namespace InvoiceApi.Contracts.Dto.Auth;

/// <summary>
/// DTO carrying user information extracted from an OAuth provider's callback.
/// After the user authenticates with Google/Microsoft/etc., the provider returns
/// these claims which we use to find or create the user in our system.
/// </summary>
public class ExternalLoginCallbackDto
{
    /// <summary>
    /// Email address from the OAuth provider's claims
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// First name from the OAuth provider's claims
    /// </summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>
    /// Last name from the OAuth provider's claims
    /// </summary>
    public string LastName { get; set; } = string.Empty;

    /// <summary>
    /// Which OAuth provider authenticated this user (Google, Microsoft, etc.)
    /// </summary>
    public EExternalProvider Provider { get; set; }

    /// <summary>
    /// Provider-specific unique user ID (e.g., Google "sub" claim, Facebook user ID).
    /// Used together with Provider to uniquely identify the OAuth user.
    /// </summary>
    public string ProviderId { get; set; } = string.Empty;
}
