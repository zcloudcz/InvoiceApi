using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace InvoiceApi.Functions;

/// <summary>
/// Catch-all HTTP trigger that routes ALL incoming HTTP requests to the ASP.NET Core pipeline.
///
/// How it works:
/// - Azure Functions receives the HTTP request and matches it to this wildcard route.
/// - With Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore (ASP.NET Core Integration),
///   the request is processed through the full ASP.NET Core middleware pipeline:
///   Authentication → Authorization → MVC Routing → Controller execution.
/// - Controllers from InvoiceApi.API are discovered via AddApplicationPart() in Program.cs.
/// - The method body below should NOT execute — the ASP.NET Core pipeline intercepts
///   the request and routes it to the matching controller action before reaching here.
///
/// Route configuration:
/// - Route = "{*route}" matches any URL path (wildcard catch-all).
/// - AuthorizationLevel.Anonymous means Azure Functions does NOT enforce its own auth keys —
///   instead, JWT authentication configured in Program.cs handles all auth via [Authorize] attributes.
/// - host.json has routePrefix="" to preserve the /api/* route structure from the original API.
///
/// Supported HTTP methods:
/// - GET, POST, PUT, DELETE, PATCH — covers all controller actions in the API.
/// </summary>
public class HttpTriggerFunction
{
    /// <summary>
    /// Entry point for all HTTP requests routed through Azure Functions.
    /// The ASP.NET Core integration handles actual routing to controllers.
    /// </summary>
    [Function("CatchAll")]
    public IActionResult Run(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "get", "post", "put", "delete", "patch",
            Route = "{*route}")]
        HttpRequest req)
    {
        // This code should never execute when ASP.NET Core Integration is properly configured.
        // The ASP.NET Core middleware pipeline intercepts the request and routes it to the
        // matching controller action. If this line IS reached, it means the integration is
        // misconfigured or no controller matched the requested route.
        return new NotFoundObjectResult(new
        {
            message = "No controller matched the requested route.",
            path = req.Path.Value,
            method = req.Method
        });
    }
}
