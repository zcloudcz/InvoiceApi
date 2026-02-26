// ============================================================================
// CorrelationIdMiddleware — Reads or generates a unique CorrelationId for each
// HTTP request in Azure Functions. Same concept as the API middleware but uses
// the IFunctionsWorkerMiddleware interface (Azure Functions Isolated Worker pattern).
//
// This enables end-to-end request tracing:
//   Blazor WASM → API/Functions → DatabaseLogger (AppLog) → Application Insights
//
// The CorrelationId is:
// 1. Read from the "X-Correlation-Id" request header (sent by Blazor WASM)
// 2. If missing, a new GUID is generated (direct API calls, health checks, etc.)
// 3. Stored in HttpContext.Items["CorrelationId"] for downstream access
// 4. Written to the AsyncLocal on DatabaseLoggerProvider so all log entries include it
// 5. Added to response headers for client-side debugging (browser DevTools)
// ============================================================================

using InvoiceApi.Infrastructure.Logging;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;

namespace InvoiceApi.Functions.Middleware;

/// <summary>
/// Azure Functions worker middleware that reads or generates a CorrelationId for each HTTP request.
/// Implements IFunctionsWorkerMiddleware (same pattern as CorsMiddleware and JwtAuthenticationMiddleware).
/// Must be registered FIRST in the middleware pipeline so all subsequent middleware/functions see the CorrelationId.
/// </summary>
public class CorrelationIdMiddleware : IFunctionsWorkerMiddleware
{
    /// <summary>
    /// The standard HTTP header name for CorrelationId propagation.
    /// Matches the header name used by the API middleware and the Blazor DelegatingHandler.
    /// </summary>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>
    /// Called for every function invocation (HTTP triggers, timer triggers, etc.).
    /// For non-HTTP triggers (timers), httpContext is null — we still set the AsyncLocal
    /// so that timer-triggered log entries get a CorrelationId.
    /// </summary>
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        // GetHttpContext() returns null for non-HTTP triggers (e.g., TimerTrigger).
        // For timer triggers, we generate a CorrelationId so log entries from that
        // invocation can still be grouped together.
        var httpContext = context.GetHttpContext();
        string correlationId;

        if (httpContext != null)
        {
            // HTTP trigger — try to read the header from the incoming request
            correlationId = httpContext.Request.Headers[HeaderName].FirstOrDefault()
                            ?? Guid.NewGuid().ToString();

            // Store in HttpContext.Items for downstream middleware/services
            httpContext.Items["CorrelationId"] = correlationId;

            // Add to response headers so the Blazor client can see it in browser DevTools
            httpContext.Response.OnStarting(() =>
            {
                httpContext.Response.Headers[HeaderName] = correlationId;
                return Task.CompletedTask;
            });
        }
        else
        {
            // Non-HTTP trigger (timer, queue, etc.) — generate a new CorrelationId
            // so all log entries from this invocation can be grouped together
            correlationId = Guid.NewGuid().ToString();
        }

        // Set the AsyncLocal so DatabaseLogger includes this CorrelationId in all AppLog entries.
        // AsyncLocal flows with async execution context — no locking needed.
        DatabaseLoggerProvider.CurrentCorrelationId.Value = correlationId;

        try
        {
            // Continue to the next middleware / function invocation
            await next(context);
        }
        finally
        {
            // Clear the AsyncLocal to prevent CorrelationId leakage between invocations
            DatabaseLoggerProvider.CurrentCorrelationId.Value = null;
        }
    }
}
