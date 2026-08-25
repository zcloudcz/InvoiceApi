namespace Fakvio.McpServer.Client;

/// <summary>
/// Supplies the JWT bearer token that <see cref="AuthHeaderHandler"/> puts on
/// each outgoing API request.
///
/// Why an abstraction instead of a captured string?
/// The MCP server has two hosting modes with two different credential sources:
///   - stdio  — one process serves exactly one user, token comes from the
///              FAKVIO_API_TOKEN environment variable (<see cref="EnvironmentApiTokenProvider"/>).
///   - HTTP   — one process serves many users, so the token must come from the
///              current session, never from a value captured at startup.
/// Resolving the token per request (instead of baking it into
/// <c>HttpClient.DefaultRequestHeaders</c> at startup) is what keeps one user's
/// credential from ending up on another user's call.
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
    /// </summary>
    string? GetToken();
}
