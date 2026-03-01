using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Fakvio.Infrastructure.Authentication;

/// <summary>
/// Custom OAuth 2.0 authentication handler for Seznam.cz.
/// Extends the standard OAuthHandler to handle Seznam.cz-specific user info response format.
///
/// OAuth flow:
/// 1. User clicks "Login with Seznam.cz" → redirected to login.szn.cz
/// 2. User authenticates → Seznam.cz redirects back with authorization code
/// 3. This handler exchanges the code for an access token (automatic via base class)
/// 4. CreateTicketAsync fetches user info from Seznam.cz API and builds ClaimsIdentity
/// </summary>
public class SeznamAuthenticationHandler : OAuthHandler<SeznamAuthenticationOptions>
{
    public SeznamAuthenticationHandler(
        IOptionsMonitor<SeznamAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <summary>
    /// Called after the OAuth token exchange is complete.
    /// Fetches user profile data from Seznam.cz API and creates the authentication ticket
    /// with the user's claims (email, name, provider ID).
    /// </summary>
    protected override async Task<AuthenticationTicket> CreateTicketAsync(
        ClaimsIdentity identity,
        AuthenticationProperties properties,
        OAuthTokenResponse tokens)
    {
        // Call the Seznam.cz user info endpoint with the access token
        var request = new HttpRequestMessage(HttpMethod.Get, Options.UserInformationEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await Backchannel.SendAsync(request, Context.RequestAborted);

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError(
                "Failed to fetch user info from Seznam.cz. Status: {StatusCode}",
                response.StatusCode);
            throw new HttpRequestException(
                $"Failed to retrieve Seznam.cz user information ({response.StatusCode}).");
        }

        // Parse the JSON response from Seznam.cz
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // Run the standard claim mapping defined in SeznamAuthenticationOptions
        // (MapJsonKey for oauth_user_id, email, firstname, lastname)
        var context = new OAuthCreatingTicketContext(
            new ClaimsPrincipal(identity),
            properties,
            Context,
            Scheme,
            Options,
            Backchannel,
            tokens,
            payload.RootElement);

        context.RunClaimActions();

        await Events.CreatingTicket(context);

        return new AuthenticationTicket(context.Principal!, context.Properties, Scheme.Name);
    }
}
