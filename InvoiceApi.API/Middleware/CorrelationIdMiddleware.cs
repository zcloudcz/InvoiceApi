using InvoiceApi.Infrastructure.Logging;

namespace InvoiceApi.API.Middleware;

/// <summary>
/// Middleware that reads (or generates) a CorrelationId for each HTTP request.
///
/// How it works:
/// 1. Checks the incoming request for an "X-Correlation-Id" header (set by Blazor WASM).
/// 2. If the header is missing, generates a new GUID — every request gets a CorrelationId.
/// 3. Stores the CorrelationId in HttpContext.Items["CorrelationId"] for use by other middleware/services.
/// 4. Sets DatabaseLoggerProvider.CurrentCorrelationId (AsyncLocal) so that DatabaseLogger
///    automatically includes the CorrelationId in all AppLog entries created during this request.
/// 5. Adds the CorrelationId to the response headers so the client can see it in browser DevTools.
///
/// Why first in the pipeline?
/// This middleware must run BEFORE any other middleware (CORS, auth, etc.) so that ALL log entries
/// generated during the request — including those from auth failures — have a CorrelationId.
///
/// Thread-safety:
/// AsyncLocal is inherently thread-safe — each async execution context gets its own value.
/// The value automatically flows with async/await calls (Task continuations).
/// </summary>
public class CorrelationIdMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>
    /// The standard HTTP header name used to propagate the CorrelationId between services.
    /// Both the request header (from Blazor) and the response header (back to Blazor) use this name.
    /// </summary>
    public const string HeaderName = "X-Correlation-Id";

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Step 1: Read CorrelationId from the incoming request header, or generate a new one.
        // Blazor WASM sends this header via CorrelationIdHandler (DelegatingHandler).
        // Server-to-server calls or direct API calls may not include it — generate a new GUID in that case.
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = Guid.NewGuid().ToString();
        }

        // Step 2: Store in HttpContext.Items for other middleware/services to access.
        // This is the standard ASP.NET Core pattern for per-request ambient data.
        context.Items["CorrelationId"] = correlationId;

        // Step 3: Set the AsyncLocal so DatabaseLogger automatically picks it up.
        // AsyncLocal flows with async execution context — every await in this request scope
        // will see the same CorrelationId value. No locking needed.
        DatabaseLoggerProvider.CurrentCorrelationId.Value = correlationId;

        // Step 4: Add to response headers — visible in browser DevTools Network tab.
        // This helps developers match a UI action to its server-side log entries.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        try
        {
            // Continue to the next middleware in the pipeline
            await _next(context);
        }
        finally
        {
            // Step 5: Clear the AsyncLocal after the request completes.
            // This prevents CorrelationId leakage if the thread is reused for a different request
            // (AsyncLocal values can persist on ThreadPool threads across await boundaries).
            DatabaseLoggerProvider.CurrentCorrelationId.Value = null;
        }
    }
}

/// <summary>
/// Extension method for clean middleware registration in Program.cs.
/// Usage: app.UseCorrelationId();
/// </summary>
public static class CorrelationIdMiddlewareExtensions
{
    /// <summary>
    /// Adds the CorrelationId middleware to the HTTP pipeline.
    /// IMPORTANT: Register this as the FIRST middleware (before UseSwagger, UseCors, UseAuth, etc.)
    /// so that every log entry — including CORS errors and auth failures — gets a CorrelationId.
    /// </summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        return app.UseMiddleware<CorrelationIdMiddleware>();
    }
}
