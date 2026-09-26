using System.Net;
using System.Text.Json;
using Fakvio.McpServer.Client;
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
    ///
    /// For a <see cref="FakvioApiException"/> (#279 / N2.2), the response is shaped by its
    /// <see cref="System.Net.HttpStatusCode"/> instead of always being the generic
    /// <c>internal_error</c> — a read-only API key calling a write tool, a 404, or a validation
    /// error all used to look identical ("something crashed") to the AI client. Anything the API
    /// client did not sanitize into <see cref="FakvioApiException.SafeMessage"/> (a non-JSON body,
    /// or a JSON body without a string <c>message</c>) falls back to a fixed, generic sentence —
    /// never the raw body, which may carry internals (stack traces, SQL, IDs).
    /// </summary>
    public static string ToJson(Exception ex)
    {
        Logger.LogError(ex, "MCP tool invocation failed");

        if (ex is FakvioApiException apiEx && apiEx.StatusCode.HasValue)
        {
            // 401 and 403 always get fixed guidance text — SafeMessage would just be the API's
            // generic "Unauthorized"/"Forbidden" wording — but they are different problems with
            // different fixes (Codex review: they used to share one "forbidden" answer, which
            // told the model to create a read+write key even when the real problem was an
            // invalid/expired/revoked key that no scope change would fix):
            //   401 = the credential itself is not accepted at all → get a new key.
            //   403 = the credential IS valid but lacks permission (read-only scope or role)
            //         for this call → create a key with read+write scope.
            // 404/400/409/422 prefer SafeMessage because there the API's own domain message
            // (e.g. "Invoice not found", "Duplicate VS") is the more precise answer.
            switch (apiEx.StatusCode.Value)
            {
                case HttpStatusCode.Unauthorized:
                    return JsonSerializer.Serialize(
                        new
                        {
                            error = "unauthorized",
                            message = "The API key is invalid, expired, or has been revoked. " +
                                      "Create a new one on /settings/integrations."
                        },
                        JsonOptions);
                case HttpStatusCode.Forbidden:
                    return JsonSerializer.Serialize(
                        new
                        {
                            error = "forbidden",
                            message = "The API key is not allowed to do this — it is read-only or your role " +
                                      "lacks the permission. Create a key with read+write scope on /settings/integrations."
                        },
                        JsonOptions);
                case HttpStatusCode.NotFound:
                    return JsonSerializer.Serialize(
                        new { error = "not_found", message = apiEx.SafeMessage ?? "The requested record does not exist." },
                        JsonOptions);
                case HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity:
                    return JsonSerializer.Serialize(
                        new { error = "validation_error", message = apiEx.SafeMessage ?? "The API rejected the input." },
                        JsonOptions);
            }
        }

        return JsonSerializer.Serialize(
            new
            {
                error = "internal_error",
                message = "An internal error occurred while processing the request. Check the server log for details."
            },
            JsonOptions);
    }
}
