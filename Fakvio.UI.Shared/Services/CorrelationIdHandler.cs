namespace Fakvio.UI.Shared.Services;

/// <summary>
/// HTTP message handler (DelegatingHandler) that adds an "X-Correlation-Id" header
/// to every outgoing API request from Blazor WASM.
///
/// How it works:
/// 1. Before each API call, generates a new GUID as the CorrelationId.
/// 2. Adds it as the "X-Correlation-Id" header on the outgoing request.
/// 3. The API/Functions middleware reads this header and propagates it to:
///    - HttpContext.Items (for other middleware)
///    - DatabaseLoggerProvider.CurrentCorrelationId (AsyncLocal → all log entries)
///    - Application Insights custom properties (Functions only)
///    - Response headers (for browser DevTools)
///
/// Why a new GUID per request?
/// Each API call is a separate "unit of work" from the user's perspective.
/// If one button click triggers 3 API calls, each gets its own CorrelationId.
/// This gives fine-grained tracing — you can see exactly which API call produced which logs.
///
/// Pipeline order:
/// This handler runs BEFORE UnauthorizedRedirectHandler in the HttpClient pipeline.
/// The order is: CorrelationIdHandler → UnauthorizedRedirectHandler → actual HTTP send.
/// </summary>
public class CorrelationIdHandler : DelegatingHandler
{
    /// <summary>
    /// The standard HTTP header name for CorrelationId propagation.
    /// Same header name is used by API and Functions middleware.
    /// </summary>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>
    /// Intercepts every outgoing HTTP request and adds the X-Correlation-Id header.
    /// </summary>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Generate a new CorrelationId for this specific API call.
        // Each request gets its own ID so server-side logs can be grouped per-call.
        var correlationId = Guid.NewGuid().ToString();
        request.Headers.Add(HeaderName, correlationId);

        // Continue the pipeline — the request now carries the CorrelationId header
        return base.SendAsync(request, cancellationToken);
    }
}
