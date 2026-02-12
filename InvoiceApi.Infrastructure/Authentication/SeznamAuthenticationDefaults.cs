namespace InvoiceApi.Infrastructure.Authentication;

/// <summary>
/// Default values for Seznam.cz OAuth authentication.
/// Contains the scheme name and Seznam.cz OAuth endpoint URLs.
/// </summary>
public static class SeznamAuthenticationDefaults
{
    /// <summary>
    /// Authentication scheme name used in .AddAuthentication().AddSeznam("Seznam", ...)
    /// </summary>
    public const string AuthenticationScheme = "Seznam";

    /// <summary>
    /// Display name shown in authentication provider lists
    /// </summary>
    public static readonly string DisplayName = "Seznam.cz";

    /// <summary>
    /// Seznam.cz OAuth 2.0 authorization endpoint — user is redirected here to log in
    /// </summary>
    public static readonly string AuthorizationEndpoint = "https://login.szn.cz/api/v1/oauth/auth";

    /// <summary>
    /// Seznam.cz OAuth 2.0 token endpoint — exchanges authorization code for access token
    /// </summary>
    public static readonly string TokenEndpoint = "https://login.szn.cz/api/v1/oauth/token";

    /// <summary>
    /// Seznam.cz user info endpoint — returns user profile data (email, name, etc.)
    /// </summary>
    public static readonly string UserInformationEndpoint = "https://login.szn.cz/api/v1/user";
}
