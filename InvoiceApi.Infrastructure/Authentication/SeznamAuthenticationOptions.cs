using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;

namespace InvoiceApi.Infrastructure.Authentication;

/// <summary>
/// Configuration options for Seznam.cz OAuth authentication.
/// Extends OAuthOptions which provides standard OAuth 2.0 configuration properties
/// (ClientId, ClientSecret, CallbackPath, AuthorizationEndpoint, TokenEndpoint, etc.)
/// </summary>
public class SeznamAuthenticationOptions : OAuthOptions
{
    public SeznamAuthenticationOptions()
    {
        // Set the Seznam.cz-specific endpoint URLs
        AuthorizationEndpoint = SeznamAuthenticationDefaults.AuthorizationEndpoint;
        TokenEndpoint = SeznamAuthenticationDefaults.TokenEndpoint;
        UserInformationEndpoint = SeznamAuthenticationDefaults.UserInformationEndpoint;

        // Request basic profile and email scopes from Seznam.cz
        Scope.Add("identity");

        // Default callback path — must match the path registered in Seznam.cz developer portal
        CallbackPath = new Microsoft.AspNetCore.Http.PathString("/api/auth/seznam-callback");

        // Map standard claims from Seznam.cz user info response
        ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.NameIdentifier, "oauth_user_id");
        ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.Email, "email");
        ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.GivenName, "firstname");
        ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.Surname, "lastname");
    }
}
