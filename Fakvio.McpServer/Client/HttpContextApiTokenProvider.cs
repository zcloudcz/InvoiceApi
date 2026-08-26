using Microsoft.AspNetCore.Http;

namespace Fakvio.McpServer.Client;

/// <summary>
/// HTTP-mode token provider: the credential is whatever bearer token the caller put on
/// the request being served right now, read from the ambient <see cref="HttpContext"/>.
///
/// <para>
/// The MCP server does not mint or store credentials of its own — it forwards the caller's
/// API key (see issue #236, <c>fak_…</c> keys authenticate the REST API the same way a JWT
/// does). So "the token for this outgoing call" is literally "the token on the incoming
/// call", and nothing has to be kept between requests.
/// </para>
///
/// <para>
/// <b>Why a singleton with no state.</b> This class is registered with
/// <c>AddSingleton</c> and keeps nothing in a field on purpose — see the registration rule
/// on <see cref="IApiTokenProvider"/>. <c>AddScoped</c> would be captured by the pooled
/// <c>IHttpClientFactory</c> pipeline and then serve one tenant's token on another
/// tenant's call. <see cref="IHttpContextAccessor"/> is itself a singleton backed by
/// <c>AsyncLocal</c>, so reading it inside <see cref="GetToken"/> resolves the value in
/// the logical context of the call being sent, which is exactly what is needed.
/// </para>
///
/// <para>
/// <b>Why this is enough here.</b> Reading <c>HttpContext</c> only works while the tool is
/// executing on the execution context of the HTTP request that carried it. The host
/// guarantees that by pinning the Streamable HTTP transport to
/// <c>HttpServerSessionMode.Stateless</c> (see <c>McpHttpHost</c>): every request builds a
/// fresh server context and the tool handler runs inside that request. A transport that ran
/// tool calls on a session-wide context instead would see no <c>HttpContext</c> at all and
/// send the call out unauthenticated — a loud 401, not a silent leak, but still a reason to
/// state the session mode rather than inherit it.
/// </para>
/// </summary>
public sealed class HttpContextApiTokenProvider : IApiTokenProvider
{
    /// <summary>The one authentication scheme the API understands, for both JWTs and API keys.</summary>
    private const string BearerPrefix = "Bearer ";

    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextApiTokenProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc />
    public string? GetToken()
    {
        // No ambient request (background work, or a stateful session processing a message
        // outside its originating request) → no credential. The call then goes out
        // unauthenticated and the API answers 401, which is the safe direction to fail.
        var header = _httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();

        if (string.IsNullOrWhiteSpace(header)
            || !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var token = header[BearerPrefix.Length..].Trim();

        return token.Length == 0 ? null : token;
    }
}
