// ============================================================================
// ApiKeyAuthenticationMiddleware — the Functions-host mirror of the API host's
// ApiKeyAuthenticationHandler + ApiKeyScopeMiddleware.
//
// Why a middleware instead of the authentication scheme: the isolated worker never
// calls UseAuthentication(), so AddFakvioAuthentication() only REGISTERS the schemes
// here — nothing ever runs them (the same reason JwtAuthenticationMiddleware exists).
//
// Both hosts share the actual logic: IApiKeyAuthenticator validates the key and builds
// the principal, ApiKeyRequestGuard decides what that principal may do. This file is
// only the driver (see CLAUDE.md — "API + Functions duplication").
// ============================================================================

using Fakvio.Application.Service;
using Fakvio.Infrastructure.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fakvio.Functions.Middleware;

/// <summary>
/// Authenticates requests presenting an API key ("Authorization: Bearer fak_…") and
/// enforces the key's scopes. Requests carrying anything else are untouched and fall
/// through to <see cref="JwtAuthenticationMiddleware"/>, which runs next.
/// </summary>
public class ApiKeyAuthenticationMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ILogger<ApiKeyAuthenticationMiddleware> _logger;

    public ApiKeyAuthenticationMiddleware(ILogger<ApiKeyAuthenticationMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        // Timer triggers have no HttpContext — nothing to authenticate.
        var httpContext = context.GetHttpContext();
        if (httpContext is null)
        {
            await next(context);
            return;
        }

        var rawKey = ApiKeyAuthenticationDefaults.ExtractRawKey(httpContext.Request.Headers.Authorization);
        if (rawKey is null)
        {
            await next(context);
            return;
        }

        // Worker-scope DI, same as the rest of this pipeline. The authenticator needs the
        // scoped MasterDbContext, which is why it is resolved per request and not injected.
        var authenticator = context.InstanceServices.GetRequiredService<IApiKeyAuthenticator>();
        var principal = await authenticator.AuthenticateAsync(rawKey, httpContext.RequestAborted);

        if (principal is null)
        {
            // Fail closed by leaving the request anonymous: every generated function
            // wrapper starts with an IsAuthenticated check and answers 401. Writing the
            // 401 here instead would bypass the [Authorize]-free anonymous endpoints,
            // which must keep working even when someone waves a dead key at them.
            _logger.LogWarning("API key rejected for {Method} {Path}",
                httpContext.Request.Method, httpContext.Request.Path.Value);
            await next(context);
            return;
        }

        httpContext.User = principal;

        // Scope enforcement sits here rather than in its own middleware: unlike the API
        // host, this pipeline has no separate authentication step to slot it behind, and
        // the guard needs nothing but the principal that was just set.
        if (await ApiKeyRequestGuard.TryRejectAsync(httpContext))
            return;

        await next(context);
    }
}
