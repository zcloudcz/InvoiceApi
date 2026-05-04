// ============================================================================
// GlobalExceptionMiddleware — Catches ALL unhandled exceptions in the ASP.NET
// Core pipeline and logs them to DatabaseLogger (→ AppLog DB table).
//
// Without this middleware, exceptions thrown in controllers or other middleware
// are caught by the ASP.NET Core runtime internally. They are written only to
// console/debug providers — they never reach DatabaseLogger, so AppLog stays
// empty while the client-side log reports errors.
//
// This middleware MUST be registered as the SECOND middleware in Program.cs
// (right after CorrelationId, which must be first so every log entry gets an ID).
// By wrapping the entire pipeline it catches exceptions from ALL sources:
// controllers, services, other middleware.
//
// Junior note: Think of this as a giant try-catch around the entire HTTP request.
// Any exception that isn't caught by controller code, service code, or other
// middleware will bubble up here and be logged + returned as a proper JSON 500.
// ============================================================================

using System.Net;
using System.Text.Json;

namespace Fakvio.API.Middleware;

/// <summary>
/// Global exception handler middleware for ASP.NET Core.
/// Catches unhandled exceptions, logs them via ILogger (→ DatabaseLogger → AppLog),
/// and returns a structured 500 JSON response. Stack trace is included only in
/// Development to avoid leaking internal details to external clients in Production.
/// </summary>
public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;
    private readonly IHostEnvironment _env;

    public GlobalExceptionMiddleware(
        RequestDelegate next,
        ILogger<GlobalExceptionMiddleware> logger,
        IHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            // Read the CorrelationId that CorrelationIdMiddleware stored in HttpContext.Items.
            // CorrelationId runs before this middleware so the value is always present.
            var correlationId = context.Items["CorrelationId"] as string ?? "unknown";

            // Log the full exception to DatabaseLogger → AppLog DB table.
            // LogError sends the full Exception object so DatabaseLogger can persist
            // the stack trace together with the message.
            _logger.LogError(ex,
                "Unhandled exception [{CorrelationId}] {Method} {Path}: {ErrorType}: {ErrorMessage}",
                correlationId,
                context.Request.Method,
                context.Request.Path,
                ex.GetType().Name,
                ex.Message);

            // Only write a response if ASP.NET hasn't started streaming the body yet.
            // If headers are already sent we cannot change the status code.
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                context.Response.ContentType = "application/json";

                // Build the error response body.
                // In Development we include exception details to aid local debugging.
                // In Production we return only a generic message to avoid leaking internals.
                object body = _env.IsDevelopment()
                    ? new
                    {
                        message = "An internal server error occurred.",
                        correlationId,
                        error = ex.GetType().Name,
                        detail = ex.Message,
                        stackTrace = ex.StackTrace
                    }
                    : new
                    {
                        message = "An internal server error occurred. Check the application log for details.",
                        correlationId
                    };

                var json = JsonSerializer.Serialize(body, new JsonSerializerOptions
                {
                    // camelCase matches the rest of the API responses
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                await context.Response.WriteAsync(json);
            }
        }
    }
}

/// <summary>
/// Extension method for clean middleware registration in Program.cs.
/// Usage: app.UseGlobalExceptionHandler();
/// IMPORTANT: Register this AFTER UseCorrelationId() so the CorrelationId is
/// already set in HttpContext.Items when an exception is caught.
/// </summary>
public static class GlobalExceptionMiddlewareExtensions
{
    /// <summary>
    /// Adds the global exception handler to the HTTP pipeline.
    /// Register early (second, right after UseCorrelationId) so it wraps all
    /// other middleware and controllers.
    /// </summary>
    public static IApplicationBuilder UseGlobalExceptionHandler(this IApplicationBuilder app)
    {
        return app.UseMiddleware<GlobalExceptionMiddleware>();
    }
}
