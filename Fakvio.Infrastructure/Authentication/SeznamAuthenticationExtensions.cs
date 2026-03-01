using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Fakvio.Infrastructure.Authentication;

/// <summary>
/// Extension methods for adding Seznam.cz OAuth authentication to the ASP.NET Core pipeline.
/// Usage in Program.cs:
///   builder.Services.AddAuthentication()
///       .AddSeznam("Seznam", options => {
///           options.ClientId = "...";
///           options.ClientSecret = "...";
///       });
/// </summary>
public static class SeznamAuthenticationExtensions
{
    /// <summary>
    /// Adds Seznam.cz OAuth authentication with the default scheme name ("Seznam").
    /// </summary>
    public static AuthenticationBuilder AddSeznam(
        this AuthenticationBuilder builder,
        Action<SeznamAuthenticationOptions> configureOptions)
    {
        return builder.AddSeznam(
            SeznamAuthenticationDefaults.AuthenticationScheme,
            configureOptions);
    }

    /// <summary>
    /// Adds Seznam.cz OAuth authentication with a custom scheme name.
    /// </summary>
    public static AuthenticationBuilder AddSeznam(
        this AuthenticationBuilder builder,
        string authenticationScheme,
        Action<SeznamAuthenticationOptions> configureOptions)
    {
        return builder.AddSeznam(
            authenticationScheme,
            SeznamAuthenticationDefaults.DisplayName,
            configureOptions);
    }

    /// <summary>
    /// Adds Seznam.cz OAuth authentication with custom scheme name and display name.
    /// Registers SeznamAuthenticationHandler as the handler for this scheme.
    /// </summary>
    public static AuthenticationBuilder AddSeznam(
        this AuthenticationBuilder builder,
        string authenticationScheme,
        string displayName,
        Action<SeznamAuthenticationOptions> configureOptions)
    {
        return builder.AddOAuth<SeznamAuthenticationOptions, SeznamAuthenticationHandler>(
            authenticationScheme,
            displayName,
            configureOptions);
    }
}
