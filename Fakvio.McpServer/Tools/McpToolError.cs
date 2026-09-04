using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fakvio.McpServer.Tools;

/// <summary>
/// Central error-handling helper shared by every MCP tool's catch-all handler.
///
/// Fixes issue #279: each tool used to do a bare
/// <c>catch (Exception ex) { error = ex.Message }</c>, which leaked whatever
/// <see cref="Fakvio.McpServer.Client.FakvioApiClient"/>'s EnsureSuccessAsync put into the
/// exception message — including the raw API error response body (stack traces, SQL
/// details, internal IDs). Routing every tool's catch-all through this one helper means
/// the "what is safe to tell the AI client" decision lives in one place, not in 37 copies.
///
/// The shape every tool follows (written down once here instead of being repeated as a
/// comment in all 37 tools):
///
/// <code>
/// try { /* call the API, serialize the result */ }
/// catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
/// catch (Exception ex) { return McpToolError.ToJson(ex); }
/// </code>
///
/// Junior notes on why it is written exactly like that:
///
/// 1. <see cref="OperationCanceledException"/> is deliberately NOT handled by this helper.
///    A request the caller cancelled is not a domain error, so the tool rethrows it.
/// 2. The <c>when (ct.IsCancellationRequested)</c> filter is load-bearing, not decoration.
///    <see cref="TaskCanceledException"/> derives from OperationCanceledException, and
///    HttpClient throws it on its OWN timeout too (100 s by default) — in that case the
///    caller's token was never cancelled. Without the filter a slow or stuck API would throw
///    across the MCP boundary and kill the whole tool call; with it, that case falls through
///    to <see cref="ToJson"/> and comes back as an ordinary sanitized error.
///    Do not compare <c>ex.CancellationToken</c> with <c>ct</c> instead — that breaks as
///    soon as anything links tokens together.
/// 3. Deserializing the model's own JSON argument belongs in its own small try block placed
///    BEFORE the one above, so that a <see cref="JsonException"/> from it can still be
///    reported precisely. It must not share a try block with the API call, because
///    FakvioApiClient deserializes API *responses* as well: a JsonException from a corrupted
///    successful response has to reach the sanitized catch-all, not be echoed back to the
///    model as "your input is malformed" with the exception text (and its JSON path and
///    byte position) attached.
/// </summary>
public static class McpToolError
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Set once from Program.cs after the host is built. Tool methods are static
    /// (required by [McpServerTool]), so a shared logger instance is simpler than adding
    /// an ILogger parameter to all 37 tool method signatures for one cross-cutting concern.
    /// Defaults to a no-op logger so tests that call tool methods directly (without running
    /// Program.cs) don't need any setup.
    /// </summary>
    public static ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>
    /// Logs the full exception server-side (stderr — MCP uses stdout for the protocol
    /// stream, see Program.cs) and returns a neutral, stable error JSON that is safe to
    /// send across the MCP boundary to an external AI client.
    /// </summary>
    public static string ToJson(Exception ex)
    {
        Logger.LogError(ex, "MCP tool invocation failed");
        return JsonSerializer.Serialize(
            new
            {
                error = "internal_error",
                message = "An internal error occurred while processing the request. Check the server log for details."
            },
            JsonOptions);
    }
}
