using System.Net.Http.Headers;
using Fakvio.McpServer.Configuration;

namespace Fakvio.McpServer.Client;

/// <summary>
/// HTTP message handler (DelegatingHandler) that stamps the Authorization
/// header on every outgoing API request, using the token that
/// <see cref="IApiTokenProvider"/> resolves at that moment.
///
/// Why per request and not once at startup?
/// <c>HttpClient.DefaultRequestHeaders</c> is shared by every caller of that
/// client. Setting the token there means whatever credential was present when
/// the process booted is sent on all later calls — under HTTP hosting that is a
/// cross-tenant leak. Mutating those defaults per call is not a fix either: the
/// client is shared, so two concurrent calls would race over the same header.
/// The request object, by contrast, belongs to exactly one call.
///
/// Junior note: DelegatingHandlers form a pipeline. This one modifies the
/// request and then calls <c>base.SendAsync</c>, which passes it to the next
/// handler (ultimately the one that performs the real network send).
/// </summary>
public sealed class AuthHeaderHandler : DelegatingHandler
{
    /// <summary>
    /// Name of the internal header proving to the API that a request really came from this MCP
    /// host (ADR 0001, docs/adr/0001-mcp-oauth21.md §4.4, threat T6 — confused deputy /
    /// token passthrough). MUST match <c>Fakvio.Infrastructure.Authentication
    /// .ApiKeyAuthenticationDefaults.ResourceProofHeaderName</c> on the API side exactly;
    /// duplicated as a literal here because this project has no reference to Fakvio.Infrastructure.
    /// </summary>
    internal const string ResourceProofHeaderName = "X-Fakvio-Resource-Proof";

    private readonly IApiTokenProvider _tokenProvider;
    private readonly McpServerSettings _settings;

    public AuthHeaderHandler(IApiTokenProvider tokenProvider, McpServerSettings settings)
    {
        _tokenProvider = tokenProvider;
        _settings = settings;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = _tokenProvider.GetToken();

        // Assigned unconditionally, including the null case: "no token" must
        // CLEAR the header, not merely leave whatever is already on the request.
        // Nothing builds a request with an Authorization header today, but under
        // the HTTP transport the incoming header travels with the call, and a
        // conditional set would let it ride out to the API unchecked.
        //
        // No token → the request goes out unauthenticated and the API answers
        // 401. Throwing here instead would turn a recoverable auth problem into
        // an opaque client-side crash inside the MCP tool call.
        request.Headers.Authorization = string.IsNullOrWhiteSpace(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token);

        // Added on every outgoing request, not only when the caller's credential happens to be
        // an OAuth token: the API only enforces this header for OAuth-issued access tokens and
        // ignores it for a manually created "fak_live_…" key, so there is no case where sending
        // it is wrong — and a per-request "is this an OAuth token" check would need to inspect
        // the (opaque) token itself, which this handler has no business doing.
        request.Headers.Remove(ResourceProofHeaderName);
        if (!string.IsNullOrEmpty(_settings.ResourceProofSecret))
            request.Headers.TryAddWithoutValidation(ResourceProofHeaderName, _settings.ResourceProofSecret);

        return base.SendAsync(request, cancellationToken);
    }
}
