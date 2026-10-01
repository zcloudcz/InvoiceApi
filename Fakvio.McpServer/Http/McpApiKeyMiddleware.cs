using Fakvio.McpServer.Client;
using Fakvio.McpServer.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Fakvio.McpServer.Http;

/// <summary>
/// Gate in front of the MCP endpoints: every HTTP request must carry a credential the
/// Fakvio API accepts, or it never reaches the MCP transport.
///
/// <para>
/// <b>Why it asks the API instead of validating locally.</b> The MCP server has no database
/// and no key material — the API owns both. Calling <c>GET /api/api-key/me</c> on every
/// request is the whole point: a key revoked one second ago stops working on the next
/// request. Caching the answer would make revocation eventually-consistent, which story #144
/// rules out explicitly.
/// </para>
///
/// <para>
/// The call goes through the very same authenticated <see cref="IFakvioApiClient"/> the tools
/// use, so the credential that gets validated is by construction the credential the tools
/// will send — there is no second code path that could disagree.
/// </para>
///
/// <para>
/// Registered with <c>app.Use(...)</c> over the whole pipeline rather than on the MCP route
/// only: this host serves nothing but MCP, and guarding everything means a future endpoint is
/// closed until somebody deliberately opens it, instead of open until somebody notices.
/// </para>
/// </summary>
public static class McpApiKeyMiddleware
{
    /// <summary>
    /// Validates the caller's credential and either forwards the request or answers 401.
    ///
    /// Junior note: this signature (<c>HttpContext</c> + the rest of the pipeline) is what
    /// <c>app.Use(...)</c> expects. Calling <paramref name="next"/> continues to the MCP
    /// endpoint; returning without calling it ends the request right here.
    /// </summary>
    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Protected Resource Metadata is how an MCP client discovers the authorization server
        // BEFORE it has any credential at all (ADR 0001 §1.3 "Discovery") — gating it would be
        // a chicken-and-egg problem, not a security boundary.
        //
        // Matched by EXACT path (GET only — the two PRM routes are GET-mapped in McpHttpHost),
        // not a "/.well-known" prefix (Codex review finding): this host serves nothing else
        // today, but a prefix bypass would silently make any FUTURE endpoint placed under that
        // namespace public too, without anyone touching this file to notice.
        if (HttpMethods.IsGet(context.Request.Method) &&
            (context.Request.Path.Equals("/.well-known/oauth-protected-resource/mcp", StringComparison.OrdinalIgnoreCase) ||
             context.Request.Path.Equals("/.well-known/oauth-protected-resource", StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        // Fast path: no credential at all cannot possibly validate, so do not spend an API
        // round-trip on it. Anything else — wrong scheme, expired key, garbage — is decided
        // by the API below, so this middleware never has to know what a valid key looks like.
        if (StringValues.IsNullOrEmpty(context.Request.Headers.Authorization))
        {
            Challenge(context, "no Authorization header");
            return;
        }

        var identity = await context.RequestServices
            .GetRequiredService<IFakvioApiClient>()
            .GetIdentityAsync(context.RequestAborted);

        // Null = the API refused the credential (unknown, expired or revoked key).
        // A transport failure is NOT swallowed here: it throws and surfaces as a 500, because
        // "the API is unreachable" and "your key is invalid" are different problems and
        // answering 401 to the first one sends the operator hunting in the wrong place.
        if (identity is null)
        {
            Challenge(context, "the API did not accept the presented credential");
            return;
        }

        // Audience check (ADR §4.4/§4.6, threat T6): an OAuth-issued access token is only good
        // for the resource it was granted for. A manually created API key (OAuthGrantId null)
        // has no resource binding at all and skips this — it is a deliberate exception (§4.4),
        // not an oversight.
        //
        // Deliberately NOT gated on this host's own OAuthEnabled flag (Codex review finding):
        // the API is what decided this credential is OAuth-backed, so if it says so, the
        // audience must hold regardless of whether THIS host's local discovery/challenge flag
        // happens to be on — two independently misconfigured flags must not turn into "one of
        // them being off silently disables an unrelated security check."
        var settings = context.RequestServices.GetRequiredService<McpServerSettings>();
        if (identity.OAuthGrantId is not null)
        {
            var ownResource = $"{settings.PublicUrl?.TrimEnd('/')}{McpHttpHost.EndpointPath}";
            if (!string.Equals(identity.OAuthResource, ownResource, StringComparison.Ordinal))
            {
                Challenge(context, $"OAuth token resource '{identity.OAuthResource}' does not match this server's resource '{ownResource}'");
                return;
            }
        }

        await next(context);
    }

    /// <summary>
    /// 401 plus the <c>WWW-Authenticate</c> header, which is what tells an MCP client the
    /// request failed on authentication rather than on the tool call itself.
    /// No detail in the body — an unauthenticated caller learns nothing beyond "not accepted";
    /// the reason goes to the log, where the operator can see it.
    ///
    /// Junior note: the logger is resolved here rather than at the top of
    /// <see cref="InvokeAsync"/> so that an accepted request — the common case — does not pay
    /// for a logger it never uses.
    /// </summary>
    private static void Challenge(HttpContext context, string reason)
    {
        context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(McpApiKeyMiddleware))
            .LogInformation("MCP request rejected: {Reason}.", reason);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;

        // Flag off ⇒ the bare "Bearer" challenge, byte-for-byte what today's clients already
        // get (T15) — resource_metadata is what tells an OAuth-aware client to start the
        // sign-in flow (ADR §1.3 "Discovery"), so pointing it at a metadata document that 404s
        // would be worse than not mentioning OAuth at all.
        var settings = context.RequestServices.GetRequiredService<McpServerSettings>();
        context.Response.Headers.WWWAuthenticate = settings.OAuthEnabled
            ? $"Bearer resource_metadata=\"{settings.PublicUrl?.TrimEnd('/')}/.well-known/oauth-protected-resource/mcp\", scope=\"read write\""
            : "Bearer";
    }
}
