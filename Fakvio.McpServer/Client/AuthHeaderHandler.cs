using System.Net.Http.Headers;

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
    private readonly IApiTokenProvider _tokenProvider;

    public AuthHeaderHandler(IApiTokenProvider tokenProvider)
    {
        _tokenProvider = tokenProvider;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = _tokenProvider.GetToken();

        // No token → send unauthenticated and let the API return 401. Failing
        // here instead would turn a recoverable auth problem into an opaque
        // client-side crash inside the MCP tool call.
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
