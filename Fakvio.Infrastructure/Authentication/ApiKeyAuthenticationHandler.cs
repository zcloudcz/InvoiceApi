using System.Text.Encodings.Web;
using Fakvio.Application.Service;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fakvio.Infrastructure.Authentication;

/// <summary>
/// ASP.NET Core driver for API-key authentication (API host only — the Functions
/// isolated worker never calls UseAuthentication(), so it has its own middleware
/// driving the same <see cref="IApiKeyAuthenticator"/>).
///
/// Deliberately contains no validation: it extracts the key, delegates, and maps the
/// answer onto an <see cref="AuthenticateResult"/>.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var rawKey = ApiKeyAuthenticationDefaults.ExtractRawKey(Request.Headers.Authorization);

        // NoResult, not Fail: nothing here claims to be an API key, so other schemes
        // (and anonymous endpoints) must be free to handle the request.
        if (rawKey is null)
            return AuthenticateResult.NoResult();

        // Resolved per request, not injected: the authenticator depends on the scoped
        // MasterDbContext, while the handler itself is created per scheme.
        var authenticator = Context.RequestServices.GetRequiredService<IApiKeyAuthenticator>();

        var principal = await authenticator.AuthenticateAsync(rawKey, Context.RequestAborted);

        // Fail (not NoResult) so an unknown / revoked / expired key is a hard 401 rather
        // than silently degrading into an anonymous request.
        return principal is null
            ? AuthenticateResult.Fail("Invalid API key.")
            : AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
