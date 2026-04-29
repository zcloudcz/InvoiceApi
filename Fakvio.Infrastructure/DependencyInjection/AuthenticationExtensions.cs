using System.Security.Claims;
using System.Text;
using Fakvio.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Fakvio.Infrastructure.DependencyInjection;

/// <summary>
/// Shared JWT authentication and OAuth provider registrations used by both
/// Fakvio.API and Fakvio.Functions. Eliminates duplication of the
/// ~80-line auth setup block between the two hosting models.
///
/// Registers:
/// - JWT Bearer authentication (default scheme)
/// - Google OAuth (conditional — only when OAuth:Google:ClientId is configured)
/// - Microsoft OAuth (conditional — only when OAuth:Microsoft:ClientId is configured)
/// - Facebook OAuth (conditional — only when OAuth:Facebook:AppId is configured)
/// - Seznam.cz OAuth (conditional — only when OAuth:Seznam:ClientId is configured)
/// - Authorization services
///
/// SECURITY: JWT secret must be configured via:
/// - Development: appsettings.Development.json or User Secrets
/// - Production: environment variable JwtSettings__Secret or Azure Key Vault
/// Never store the secret in appsettings.json (committed to Git).
/// </summary>
public static class AuthenticationExtensions
{
    /// <summary>
    /// Registers JWT Bearer authentication, conditional OAuth providers, and authorization.
    /// Call this from both API and Functions Program.cs to keep auth configuration in sync.
    ///
    /// Example usage in API:
    ///   builder.Services.AddFakvioAuthentication(builder.Configuration);
    ///
    /// Example usage in Functions:
    ///   services.AddFakvioAuthentication(config);
    /// </summary>
    /// <param name="services">The DI container to register auth services into.</param>
    /// <param name="configuration">Application configuration containing JwtSettings and OAuth sections.</param>
    /// <returns>The same IServiceCollection for chaining.</returns>
    public static IServiceCollection AddFakvioAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── JWT Configuration ───────────────────────────────────────────────
        // Read JWT settings from configuration. Secret is required; Issuer/Audience have defaults.
        var jwtSecret = configuration["JwtSettings:Secret"]
            ?? throw new InvalidOperationException(
                "JWT Secret not configured. Set it in appsettings.Development.json, " +
                "User Secrets (dotnet user-secrets set \"JwtSettings:Secret\" \"<value>\"), " +
                "local.settings.json, or environment variable JwtSettings__Secret.");
        var jwtIssuer = configuration["JwtSettings:Issuer"] ?? "Fakvio";
        var jwtAudience = configuration["JwtSettings:Audience"] ?? "FakvioClient";

        // ── JWT Bearer Authentication ───────────────────────────────────────
        // Sets JWT Bearer as the default authentication scheme.
        // All [Authorize] endpoints will require a valid Bearer token.
        // ClockSkew = Zero removes the default 5-minute tolerance for token expiry.
        var authBuilder = services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtIssuer,
                ValidAudience = jwtAudience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
                ClockSkew = TimeSpan.Zero, // Remove default 5 minute clock skew

                // CRITICAL: Tell the JWT validator which claim to use for roles.
                //
                // AuthService.GenerateJwtTokenAsync() emits role as:
                //   new Claim(ClaimTypes.Role, "SysAdmin")
                //
                // JwtSecurityTokenHandler serializes ClaimTypes.Role
                // (http://schemas.microsoft.com/ws/2008/06/identity/claims/role)
                // to the short JWT claim key "role".
                //
                // Without RoleClaimType below, ASP.NET Core's JWT middleware reads
                // the token back and leaves the claim under the short key "role"
                // instead of mapping it to ClaimTypes.Role. The result:
                //   [Authorize(Roles = "SysAdmin")] looks for ClaimTypes.Role → not found → 403
                //
                // Fix: explicitly tell TokenValidationParameters which claim is the role claim.
                // This makes ClaimsPrincipal.IsInRole("SysAdmin") return true and
                // [Authorize(Roles = "SysAdmin")] pass correctly.
                RoleClaimType = ClaimTypes.Role,

                // Tell the validator which claim holds the user's name/identifier.
                // Ensures User.Identity.Name resolves to the expected value.
                NameClaimType = ClaimTypes.Name
            };
        });

        // ── External OAuth Providers (conditional) ──────────────────────────
        // Each provider is only registered when its ClientId/AppId is configured.
        // Empty credentials cause OAuthOptions.Validate() to throw ArgumentException,
        // which would crash the authentication middleware on every request — including JWT login.

        AddOAuthProviders(authBuilder, configuration);

        // ── Authorization ───────────────────────────────────────────────────
        services.AddAuthorization();

        return services;
    }

    /// <summary>
    /// Conditionally registers external OAuth providers based on configuration.
    /// Each provider is only added when its ClientId/AppId is present and non-empty,
    /// preventing startup crashes from empty OAuth credentials.
    /// </summary>
    private static void AddOAuthProviders(
        Microsoft.AspNetCore.Authentication.AuthenticationBuilder authBuilder,
        IConfiguration configuration)
    {
        // Google OAuth
        var googleClientId = configuration["OAuth:Google:ClientId"];
        if (!string.IsNullOrEmpty(googleClientId))
        {
            authBuilder.AddGoogle("Google", o =>
            {
                o.ClientId = googleClientId;
                o.ClientSecret = configuration["OAuth:Google:ClientSecret"] ?? "";
                o.CallbackPath = "/api/auth/google-callback";
            });
        }

        // Microsoft (Azure AD / personal accounts)
        var microsoftClientId = configuration["OAuth:Microsoft:ClientId"];
        if (!string.IsNullOrEmpty(microsoftClientId))
        {
            authBuilder.AddMicrosoftAccount("Microsoft", o =>
            {
                o.ClientId = microsoftClientId;
                o.ClientSecret = configuration["OAuth:Microsoft:ClientSecret"] ?? "";
                o.CallbackPath = "/api/auth/microsoft-callback";
            });
        }

        // Facebook
        var facebookAppId = configuration["OAuth:Facebook:AppId"];
        if (!string.IsNullOrEmpty(facebookAppId))
        {
            authBuilder.AddFacebook("Facebook", o =>
            {
                o.AppId = facebookAppId;
                o.AppSecret = configuration["OAuth:Facebook:AppSecret"] ?? "";
                o.CallbackPath = "/api/auth/facebook-callback";
            });
        }

        // Seznam.cz — custom OAuth handler (Czech-specific provider)
        var seznamClientId = configuration["OAuth:Seznam:ClientId"];
        if (!string.IsNullOrEmpty(seznamClientId))
        {
            authBuilder.AddSeznam("Seznam", o =>
            {
                o.ClientId = seznamClientId;
                o.ClientSecret = configuration["OAuth:Seznam:ClientSecret"] ?? "";
                o.CallbackPath = "/api/auth/seznam-callback";
            });
        }
    }
}
