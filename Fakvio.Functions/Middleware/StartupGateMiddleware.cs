// ============================================================================
// StartupGateMiddleware — answers "not ready yet" instead of failing slowly.
//
// The Functions worker reports ready the moment the host starts, but the database is
// only reachable once the Tailscale tunnel's forwarder has bound its loopback port
// (Fakvio.Functions/Program.cs). Requests that arrive in between used to hang on the
// connection string's Timeout=15, burn all three EF retries and end as HTTP 500 —
// which reads like a broken database rather than a host that is still starting.
//
// This middleware turns that window into an honest, short 503 + Retry-After, which the
// browser client retries transparently (RetryAfterHandler in Fakvio.UI.Shared).
// ============================================================================

using Fakvio.Infrastructure.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace Fakvio.Functions.Middleware;

/// <summary>
/// Blocks tenant/API traffic until <see cref="StartupState.DatabaseReady"/> is set.
///
/// Registered right after GlobalException/CorrelationId/CORS and BEFORE the auth and
/// tenant middleware: those all touch the master database, so gating later would not
/// actually avoid the failing connection.
/// </summary>
public class StartupGateMiddleware : IFunctionsWorkerMiddleware
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

    private readonly ILogger<StartupGateMiddleware> _logger;

    public StartupGateMiddleware(ILogger<StartupGateMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        // Fast path — true for the entire life of the process after the first second or so.
        if (StartupState.DatabaseReady)
        {
            await next(context);
            return;
        }

        // Non-HTTP triggers (LogFlush, ImapPoll, …) are not gated: they already tolerate an
        // unreachable database, they are not waiting on a user, and blocking them would
        // silently drop scheduled work instead of delaying a click.
        var httpContext = context.GetHttpContext();
        if (httpContext == null)
        {
            await next(context);
            return;
        }

        var path = httpContext.Request.Path.Value ?? string.Empty;
        if (path.StartsWith(DiagnosticPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        _logger.LogInformation(
            "Startup gate: {Path} answered 503 — database bring-up still in progress", path);

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers.RetryAfter = RetryAfterSeconds.ToString();
        await httpContext.Response.WriteAsJsonAsync(new
        {
            message = "The service is still starting. Please retry in a moment.",
            retryAfterSeconds = RetryAfterSeconds
        });
    }
}
