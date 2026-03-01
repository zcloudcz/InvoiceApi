// ============================================================================
// CorsFunctions — Catch-all handler for CORS preflight (OPTIONS) requests.
//
// Why this is needed:
// When the Blazor WASM app (running in the browser) makes a cross-origin request
// with custom headers (like "Authorization: Bearer ..."), the browser FIRST sends
// an OPTIONS request (called "preflight") to check if the server allows it.
//
// Our HTTP trigger functions only handle specific methods (GET, POST, PUT, DELETE).
// Without this catch-all, OPTIONS requests would get a 404 from the Functions host.
//
// How it works:
// - Route "{*path}" matches ANY URL path that no other OPTIONS function handles
// - Returns 204 No Content (standard preflight response)
// - The CorsMiddleware adds the actual CORS headers to the response
// ============================================================================

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace Fakvio.Functions.HttpFunctions;

/// <summary>
/// Handles CORS preflight (OPTIONS) requests for all API routes.
/// The actual CORS headers are added by <see cref="Middleware.CorsMiddleware"/>.
/// </summary>
public class CorsFunctions
{
    /// <summary>
    /// Catch-all OPTIONS handler. The browser sends OPTIONS before any cross-origin
    /// request that has custom headers (like Authorization). We return 204 (No Content)
    /// and let the CORS middleware add the appropriate Access-Control-* headers.
    /// </summary>
    [Function("Cors_PreFlight")]
    public IActionResult HandlePreFlight(
        [HttpTrigger(AuthorizationLevel.Anonymous, "options", Route = "{*path}")] HttpRequest req)
    {
        // 204 No Content is the standard response for CORS preflight.
        // No body needed — the browser only cares about the response headers.
        return new StatusCodeResult(StatusCodes.Status204NoContent);
    }
}
