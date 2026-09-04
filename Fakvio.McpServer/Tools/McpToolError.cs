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
/// Junior note: <see cref="OperationCanceledException"/> is NOT handled here on purpose —
/// each tool must catch it separately and `throw;` (rethrow) before falling into the
/// generic catch. A cancelled request is not a domain error and must propagate.
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
