using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Fakvio.Functions;

/// <summary>
/// Keeps the MCP HTTP host warm so that AI clients do not hit its cold-start window.
///
/// <para>
/// <b>The problem this exists for.</b> <c>Fakvio.McpServer</c> is deployed as an Azure Functions
/// custom handler (DEVGUIDE §9.1): the Functions host runs our ASP.NET Core process and proxies
/// requests to it on port 8080. On a cold start those two become ready at slightly different
/// moments — measured on 2026-09-07, the host forwarded a request at 19:09:19.846 and Kestrel
/// only reported "Now listening on: http://0.0.0.0:8080" at 19:09:20.158. A request landing in
/// that ~310 ms gap finds nothing on the port and the Functions host answers <b>500</b>.
/// </para>
///
/// <para>
/// <b>Why the fix is a ping and not a retry.</b> The 500 is produced before our code runs — there
/// is no handler, no middleware and no catch block in the MCP host at that moment, so nothing
/// there can retry it. The only ways to remove the gap are to stop the host from going cold or to
/// reserve an always-ready instance. A ping every few minutes does the first for a fraction of the
/// cost of the second, and it leaves the MCP host's own code untouched.
/// </para>
///
/// <para>
/// <b>Why this has no BackgroundService twin</b> (CLAUDE.md asks every periodic task for both a
/// worker and a Function). This one is deliberately Functions-only: it warms a deployed cloud
/// resource, and a developer running the API locally has no MCP host of their own to keep warm —
/// a worker variant would either do nothing or poke the shared cloud host from every developer
/// machine. The rule exists so that background work does not silently fail to run in one of the
/// two hosts; here there is nothing for the API host to run.
/// </para>
/// </summary>
public class McpKeepAliveFunctions
{
    /// <summary>
    /// Configuration key holding the MCP HTTP host's base address, e.g.
    /// <c>https://mcp.fakvio.cz</c>. Set per environment in the Function App's settings
    /// (as <c>McpKeepAlive__Url</c>), never committed — test and production point at
    /// different hosts.
    ///
    /// <para>
    /// Leaving it unset switches the warm-up off. That is the same on/off idiom the MCP deploy
    /// jobs use (DEVGUIDE §9.1): an environment without an MCP host stays quiet instead of
    /// logging a failure every five minutes.
    /// </para>
    /// </summary>
    private const string UrlConfigKey = "McpKeepAlive:Url";

    /// <summary>
    /// Generous enough that a cold start (measured at ~6 s) still completes, short enough that a
    /// wedged host cannot hold a function execution open for minutes at a time.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<McpKeepAliveFunctions> _logger;

    public McpKeepAliveFunctions(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<McpKeepAliveFunctions> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Every five minutes, send one request the MCP host can answer without touching the API.
    ///
    /// <para>
    /// <b>A request with no Authorization header is the right probe.</b>
    /// <c>McpApiKeyMiddleware</c> rejects it immediately, before any round-trip to
    /// <c>/api/api-key/me</c> — so the ping proves the handler process is listening without
    /// carrying a credential and without putting load on the API. The expected answer is
    /// <b>401</b>; anything else means the host is not healthy.
    /// </para>
    ///
    /// <para>
    /// Five minutes is well inside the idle window that deallocates a Flex Consumption instance,
    /// and each execution is a single sub-second request — the cost is noise next to an
    /// always-ready instance, which is billed continuously.
    /// </para>
    /// </summary>
    [Function("McpKeepAlive")]
    public async Task RunAsync(
        [TimerTrigger("0 */5 * * * *")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        var baseUrl = _configuration[UrlConfigKey];

        // No MCP host in this environment — nothing to keep warm, and nothing to complain about.
        if (string.IsNullOrWhiteSpace(baseUrl))
            return;

        var endpoint = $"{baseUrl.TrimEnd('/')}/mcp";

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = RequestTimeout;

            using var response = await client.PostAsync(endpoint, content: null, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // Debug, not Information: this runs 288 times a day and the logs go to the
                // database (DatabaseLoggerProvider). The happy path must not fill that table.
                _logger.LogDebug("MCP keep-alive ping to {Endpoint} answered 401 as expected.", endpoint);
                return;
            }

            _logger.LogWarning(
                "MCP keep-alive ping to {Endpoint} answered {StatusCode}, expected 401. " +
                "The host may be starting, misconfigured, or the API-key gate may be gone.",
                endpoint, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutting down — not a failure worth reporting.
            throw;
        }
        catch (Exception ex)
        {
            // A failed warm-up is never worth failing the function over: the next tick retries in
            // five minutes, and the only consequence of a miss is that a user might meet the cold
            // start this function exists to hide.
            _logger.LogWarning(ex, "MCP keep-alive ping to {Endpoint} failed.", endpoint);
        }
    }
}
