// ============================================================================
// GlobalExceptionMiddleware — Catches ALL unhandled exceptions in the Azure
// Functions pipeline and logs them to the DatabaseLogger (→ AppLog DB table).
//
// Without this middleware, exceptions thrown in other middleware (JWT validation,
// tenant resolution, CORS) or in function code (deserialization, controller actions)
// are caught by the Azure Functions runtime and sent ONLY to Application Insights.
// They never reach the DatabaseLogger because the runtime's exception handler
// bypasses the application's ILogger infrastructure.
//
// This middleware MUST be the FIRST middleware in the pipeline (before CorrelationId)
// so it wraps the entire request lifecycle and catches exceptions from ALL sources.
//
// Junior note: Think of this as a giant try-catch around the entire HTTP request.
// Any exception that isn't caught by controller code, service code, or other middleware
// will bubble up here and get logged to the AppLog table.
// ============================================================================

using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace Fakvio.Functions.Middleware;

/// <summary>
/// Global exception handler middleware for Azure Functions Isolated Worker.
/// Catches unhandled exceptions, logs them via ILogger (→ DatabaseLogger → AppLog),
/// and returns a 500 Internal Server Error response with a generic error message.
/// </summary>
public class GlobalExceptionMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(ILogger<GlobalExceptionMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            // Log the full exception to DatabaseLogger → AppLog DB table.
            // This is the critical fix: without this, unhandled exceptions go only
            // to App Insights (via the Functions runtime) and never reach AppLog.
            _logger.LogError(ex,
                "Unhandled exception in {FunctionName}: {ErrorType}: {ErrorMessage}",
                context.FunctionDefinition.Name,
                ex.GetType().Name,
                ex.Message);

            // Try to return a proper 500 response for HTTP triggers.
            // For non-HTTP triggers (timers), there's no HTTP response to write.
            var httpContext = context.GetHttpContext();
            if (httpContext != null && !httpContext.Response.HasStarted)
            {
                httpContext.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                httpContext.Response.ContentType = "application/json";
                await httpContext.Response.WriteAsync(
                    """{"message":"An internal server error occurred. Check the application log for details."}""");
            }
        }
    }
}
