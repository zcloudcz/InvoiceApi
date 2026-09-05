namespace Fakvio.McpServer.Client;

/// <summary>
/// Supplies the bearer credential that <see cref="AuthHeaderHandler"/> puts on
/// each outgoing API request — an API key (<c>fak_live_…</c>) or a login JWT;
/// the API tells them apart by prefix, so nothing here has to care which it is.
///
/// Why an abstraction instead of a captured string?
/// The MCP server has two hosting modes with two different credential sources:
///   - stdio  — one process serves exactly one user, token comes from the
///              FAKVIO_API_TOKEN environment variable (<see cref="EnvironmentApiTokenProvider"/>).
///   - HTTP   — one process serves many users, so the token must come from the
///              caller's own session, never from a value captured at startup.
/// Resolving the token per request (instead of baking it into
/// <c>HttpClient.DefaultRequestHeaders</c> at startup) is what keeps one user's
/// credential from ending up on another user's call.
///
/// <para>
/// <b>Registration rule — every implementation is a SINGLETON.</b> A
/// multi-user implementation must not store the credential in a field and must
/// not be registered with <c>AddScoped</c>; it reads the token from ambient
/// request-local state (<c>IHttpContextAccessor</c> — itself a singleton over
/// <c>AsyncLocal</c>) inside <see cref="GetToken"/>, so the value is resolved
/// in the logical context of the call being sent.
/// </para>
/// <para>
/// The reason is not style. <c>AddHttpMessageHandler&lt;AuthHeaderHandler&gt;()</c>
/// does not resolve the handler from the request scope: <c>IHttpClientFactory</c>
/// builds the entire pipeline in its own private scope and pools it (default
/// handler lifetime 2 minutes). A scoped provider would be captured by that
/// pooled handler on first construction and then handed to every later caller
/// for the lifetime of the pipeline — the same cross-tenant leak this interface
/// exists to prevent, just moved from <c>DefaultRequestHeaders</c> into the
/// handler.
/// </para>
///
/// Junior note: the implementation is picked once in <c>Program.cs</c> — the
/// handler and the API client never know which mode they are running in.
/// </summary>
public interface IApiTokenProvider
{
    /// <summary>
    /// Returns the bearer token to use for the request being sent right now,
    /// or <c>null</c> when no token is available (the request then goes out
    /// unauthenticated and the API answers 401).
    ///
    /// Synchronous on purpose: an ambient-context lookup has nothing to await.
    /// Do not cache the result in the implementation — that would turn a
    /// per-request credential back into a per-instance one.
    /// </summary>
    string? GetToken();
}
