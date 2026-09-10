// ============================================================================
// StartupGateMiddleware — answers "not ready yet" instead of failing slowly.
//
// Kestrel accepts requests the moment the host starts, but the database is only
// reachable once the Tailscale tunnel's forwarder has bound its loopback port
// (see the tunnel bootstrap in Program.cs). Requests that arrive in between used to
// hang on the connection string's Timeout=15, burn all EF retries and end as HTTP 500 —
// which reads like a broken database rather than a host that is still starting.
//
// This middleware turns that window into an honest, short 503 + Retry-After, which the
// browser client retries transparently (RetryAfterHandler in Fakvio.UI.Shared).
// ============================================================================

using Fakvio.Infrastructure.Service;

namespace Fakvio.API.Middleware;

/// <summary>
/// Blocks API traffic until <see cref="StartupState.DatabaseReady"/> is set.
///
/// Registered right after CorrelationId/GlobalException/CORS and BEFORE the auth and
/// tenant middleware: those all touch the master database, so gating later would not
/// actually avoid the failing connection.
/// </summary>
public class StartupGateMiddleware
{
    /// <summary>
    /// How long the client is told to wait. The tunnel normally binds in well under a
    /// second; five seconds keeps a retrying client from hammering the host in the rare
    /// case where the login needs a second or third attempt.
    /// </summary>
    private const int RetryAfterSeconds = 5;

    /// <summary>
    /// Diagnostics must stay reachable while the gate is closed — it is the only endpoint
    /// that can explain WHY the database is not ready. It is master-only and tolerates an
    /// unreachable database by design (DiagnosticController.Health reports 503 itself).
    /// </summary>
    private const string DiagnosticPrefix = "/api/diagnostic";

    private readonly RequestDelegate _next;
    private readonly ILogger<StartupGateMiddleware> _logger;

    public StartupGateMiddleware(RequestDelegate next, ILogger<StartupGateMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Fast path — true for the entire life of the process after the first second or so.
        if (StartupState.DatabaseReady)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        if (path.StartsWith(DiagnosticPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        _logger.LogInformation(
            "Startup gate: {Path} answered 503 — database bring-up still in progress", path);

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString();
        await context.Response.WriteAsJsonAsync(new
        {
            message = "The service is still starting. Please retry in a moment.",
            retryAfterSeconds = RetryAfterSeconds
        });
    }
}

public static class StartupGateMiddlewareExtensions
{
    /// <summary>Adds <see cref="StartupGateMiddleware"/> to the pipeline.</summary>
    public static IApplicationBuilder UseStartupGate(this IApplicationBuilder app)
    {
        return app.UseMiddleware<StartupGateMiddleware>();
    }
}
