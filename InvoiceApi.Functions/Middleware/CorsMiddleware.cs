// ============================================================================
// CorsMiddleware — Adds CORS headers to all HTTP responses from Azure Functions.
//
// Why this is needed:
// The Blazor WASM UI runs in the browser at a DIFFERENT origin (e.g., localhost:5145
// or a production Static Web Apps URL) than the Azure Functions API. Browsers block
// cross-origin requests unless the server responds with proper CORS headers.
//
// How it works:
// 1. Reads allowed origins from configuration (CorsSettings:AllowedOrigins)
// 2. For each incoming request, checks if the Origin header matches an allowed origin
// 3. If matched, adds Access-Control-Allow-* headers to the response
// 4. Works together with CorsFunctions.cs which handles OPTIONS preflight requests
//
// Configuration:
// - local.settings.json (development): CorsSettings__AllowedOrigins__0, __1, etc.
// - Azure Portal (production): Application Settings with same key format
// ============================================================================

using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace InvoiceApi.Functions.Middleware;

/// <summary>
/// Azure Functions worker middleware that injects CORS headers into every HTTP response.
/// This runs BEFORE and AFTER each function invocation in the worker pipeline.
/// </summary>
public class CorsMiddleware : IFunctionsWorkerMiddleware
{
    // Set of allowed origins for fast O(1) lookup.
    // Loaded once from configuration when the middleware is created (singleton lifetime).
    private readonly HashSet<string> _allowedOrigins;
    private readonly ILogger<CorsMiddleware> _logger;

    public CorsMiddleware(IConfiguration configuration, ILogger<CorsMiddleware> logger)
    {
        // Read origins from the CorsSettings:AllowedOrigins array in configuration.
        // In local.settings.json, these are flat keys like:
        //   "CorsSettings__AllowedOrigins__0": "http://localhost:5145"
        //   "CorsSettings__AllowedOrigins__1": "https://localhost:7212"
        // In Azure Portal, they're Application Settings with the same format.
        var origins = configuration.GetSection("CorsSettings:AllowedOrigins").Get<string[]>() ?? [];
        _allowedOrigins = new HashSet<string>(origins, StringComparer.OrdinalIgnoreCase);
        _logger = logger;

        if (_allowedOrigins.Count > 0)
        {
            _logger.LogInformation("CORS middleware initialized with {Count} allowed origins", _allowedOrigins.Count);
        }
        else
        {
            _logger.LogWarning("CORS middleware initialized with NO allowed origins — all cross-origin requests will be blocked");
        }
    }

    /// <summary>
    /// Called for every function invocation. Adds CORS headers if the request's Origin
    /// header matches one of the configured allowed origins.
    /// </summary>
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        // GetHttpContext() is available because we use ASP.NET Core integration
        // (ConfigureFunctionsWebApplication). It returns null for non-HTTP triggers (timers).
        var httpContext = context.GetHttpContext();

        if (httpContext != null)
        {
            var origin = httpContext.Request.Headers.Origin.ToString();

            // Only add CORS headers if the request has an Origin header AND it's in our allowed list.
            // Requests without Origin (same-origin, server-to-server) don't need CORS headers.
            if (!string.IsNullOrEmpty(origin) && _allowedOrigins.Contains(origin))
            {
                // Use OnStarting callback to set headers just before the response is sent.
                // This is safer than setting headers directly because it runs after the function
                // has finished but before bytes are written to the network.
                httpContext.Response.OnStarting(() =>
                {
                    var headers = httpContext.Response.Headers;

                    // Echo back the exact origin (not "*") — required when AllowCredentials is true.
                    headers["Access-Control-Allow-Origin"] = origin;

                    // Allow cookies and Authorization headers to be sent cross-origin.
                    headers["Access-Control-Allow-Credentials"] = "true";

                    // Allow common HTTP methods used by the API.
                    headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, PATCH, OPTIONS";

                    // Allow common headers that the Blazor WASM app sends.
                    // X-Company-Id: SysAdmin impersonation header (selects which company to operate on).
                    // X-Correlation-Id: Request tracing header (links UI action to server-side logs).
                    headers["Access-Control-Allow-Headers"] =
                        "Content-Type, Authorization, X-Requested-With, Accept, X-Company-Id, X-Correlation-Id";

                    // Expose X-Correlation-Id in response so browser JavaScript can read it.
                    // Without this, CORS blocks the browser from accessing non-standard response headers.
                    // The Blazor WASM client can use this for client-side debugging/logging.
                    headers["Access-Control-Expose-Headers"] = "X-Correlation-Id";

                    // Cache preflight results for 24 hours — reduces preflight requests.
                    headers["Access-Control-Max-Age"] = "86400";

                    return Task.CompletedTask;
                });
            }
        }

        // Continue to the next middleware / function invocation.
        await next(context);
    }
}
