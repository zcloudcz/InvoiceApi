using Fakvio.McpServer.Client;
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
        context.Response.Headers.WWWAuthenticate = "Bearer";
    }
}
