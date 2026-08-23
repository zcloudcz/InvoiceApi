using Fakvio.Infrastructure.Authentication;

namespace Fakvio.API.Middleware;

/// <summary>
/// Enforces API-key scopes on the authenticated principal (read vs read,write) and keeps
/// key management off the API-key credential. All the rules live in
/// <see cref="ApiKeyRequestGuard"/> so the Functions host applies exactly the same ones.
///
/// Must be registered AFTER UseAuthentication() — before that, HttpContext.User has no
/// scope claim and the guard would wave every request through.
/// </summary>
public class ApiKeyScopeMiddleware
{
    private readonly RequestDelegate _next;

    public ApiKeyScopeMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (await ApiKeyRequestGuard.TryRejectAsync(context))
            return;

        await _next(context);
    }
}

/// <summary>
/// Extension method to register the API-key scope middleware in the pipeline.
/// Must be called AFTER UseAuthentication().
/// </summary>
public static class ApiKeyScopeMiddlewareExtensions
{
    public static IApplicationBuilder UseApiKeyScope(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<ApiKeyScopeMiddleware>();
    }
}
