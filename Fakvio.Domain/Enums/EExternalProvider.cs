namespace Fakvio.Domain.Enums;

/// <summary>
/// External OAuth authentication provider.
/// Used to identify which third-party identity provider a user registered with.
/// Users with ExternalProvider = None use traditional email + password login.
/// </summary>
public enum EExternalProvider
{
    /// <summary>
    /// No external provider — standard email + password authentication
    /// </summary>
    None = 0,

    /// <summary>
    /// Google OAuth 2.0 (accounts.google.com)
    /// </summary>
    Google = 1,

    /// <summary>
    /// Microsoft Account / Azure AD (login.microsoftonline.com)
    /// </summary>
    Microsoft = 2,

    /// <summary>
    /// Facebook Login (facebook.com)
    /// </summary>
    Facebook = 3,

    /// <summary>
    /// Apple Sign In (appleid.apple.com)
    /// </summary>
    Apple = 4,

    /// <summary>
    /// Seznam.cz OAuth (login.szn.cz) — Czech-specific provider
    /// </summary>
    Seznam = 5
}
