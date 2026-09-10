using System.Net;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// HTTP message handler that transparently retries a 503 Service Unavailable carrying a
/// <c>Retry-After</c> header, instead of surfacing it to the user as an error.
///
/// Why this exists: the Functions host reports ready before its database path is usable
/// (the Tailscale tunnel binds its loopback port in a background task — see
/// StartupGateMiddleware), and Azure Flex Consumption cold-starts the worker regularly.
/// Every request that lands in that window used to show the user an error that went away
/// after a few manual refreshes. Retrying once or twice here makes the window invisible.
///
/// Deliberately narrow:
/// - Only 503, and only when the server actually sent Retry-After. A 503 without that
///   header is a real outage, not a "come back in a second", and must reach the user.
/// - A retry re-sends the SAME HttpRequestMessage, which only works if its body can be
///   read twice. Buffered bodies (JSON, form, byte array) can; a file upload's stream
///   cannot, so those are never retried.
/// - Hard caps on both attempts and total wait, so a permanently-down backend fails in
///   seconds rather than hanging the UI.
///
/// Retrying a POST is safe HERE specifically because StartupGateMiddleware answers before
/// any function body runs — the server did not act on the first attempt.
/// </summary>
public class RetryAfterHandler : DelegatingHandler
{
    /// <summary>Extra attempts after the first one. Three total requests at most.</summary>
    private const int MaxRetries = 2;

    /// <summary>Ceiling on the summed wait, so the UI never blocks for longer than this.</summary>
    private static readonly TimeSpan MaxTotalDelay = TimeSpan.FromSeconds(20);

    /// <summary>Used when Retry-After names a longer delay than we are willing to wait.</summary>
    private static readonly TimeSpan MaxSingleDelay = TimeSpan.FromSeconds(10);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var spent = TimeSpan.Zero;

        for (var attempt = 0; ; attempt++)
        {
            var response = await base.SendAsync(request, cancellationToken);

            if (response.StatusCode != HttpStatusCode.ServiceUnavailable
                || attempt >= MaxRetries
                || !CanResend(request))
            {
                return response;
            }

            var delay = ReadRetryAfter(response);
            if (delay == null || spent + delay.Value > MaxTotalDelay)
            {
                return response;
            }

            // The response is being discarded, so its socket has to go back to the pool —
            // otherwise a retried call leaks one connection per attempt.
            response.Dispose();

            await Task.Delay(delay.Value, cancellationToken);
            spent += delay.Value;
        }
    }

    /// <summary>
    /// Whether the request body survives being sent a second time. Buffered content does;
    /// a stream (file upload, multipart) has already been consumed and would re-send empty.
    /// </summary>
    private static bool CanResend(HttpRequestMessage request) =>
        request.Content is null or not (StreamContent or MultipartContent);

    /// <summary>
    /// Reads Retry-After as a delay. The header may be either "5" (delta seconds) or an
    /// HTTP date; HttpClient parses both into the same typed value, so the two cases only
    /// differ in which property is populated.
    /// </summary>
    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter == null)
        {
            return null;
        }

        var delay = retryAfter.Delta ?? (retryAfter.Date - DateTimeOffset.UtcNow);
        if (delay is not { } value || value <= TimeSpan.Zero)
        {
            return null;
        }

        return value > MaxSingleDelay ? MaxSingleDelay : value;
    }
}
