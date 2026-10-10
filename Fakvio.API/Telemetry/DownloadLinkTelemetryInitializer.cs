using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Http.Extensions;

namespace Fakvio.API.Telemetry;

/// <summary>
/// Hides the token of a signed download link wherever a request path is written down.
///
/// Why: in <c>GET /api/download/{token}</c> the token IS the credential (see DownloadLinkController) and
/// it sits in the URL path. Anything that records the path - the AppLog table the SysAdmin log viewer
/// shows, Application Insights - would otherwise store a credential that still works for minutes.
/// </summary>
public static class DownloadLinkLogRedactor
{
    /// <summary>Everything after this prefix is the token.</summary>
    public const string TokenPathPrefix = "/api/download/";

    private const string RedactedPath = TokenPathPrefix + "[redacted]";

    /// <summary>True when <paramref name="path"/> is a download-link redemption, i.e. contains a token.</summary>
    public static bool HasToken(string? path)
        => path != null && path.StartsWith(TokenPathPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the path with the token replaced; every other path is returned unchanged.</summary>
    public static string RedactPath(string? path) => HasToken(path) ? RedactedPath : path ?? string.Empty;
}

/// <summary>
/// Application Insights records the full URL of every request. This initializer rewrites it for
/// download-link redemptions so the token never leaves the process (same registration pattern as
/// <see cref="CorrelationIdTelemetryInitializer"/>).
/// </summary>
public class DownloadLinkTelemetryInitializer : ITelemetryInitializer
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public DownloadLinkTelemetryInitializer(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>Called for every telemetry item; only request telemetry carries the URL.</summary>
    public void Initialize(ITelemetry telemetry)
    {
        if (telemetry is not RequestTelemetry request)
            return;

        // The SDK runs initializers when the request starts (Url not filled in yet) and again when it
        // is sent. Take the URL from the telemetry when it is there, otherwise from the live request -
        // setting it early also stops the SDK from filling in the raw one later.
        var url = request.Url;
        if (url == null && _httpContextAccessor.HttpContext is { } http)
            Uri.TryCreate(http.Request.GetEncodedUrl(), UriKind.Absolute, out url);

        if (url == null || !DownloadLinkLogRedactor.HasToken(url.AbsolutePath))
            return;

        request.Url = new UriBuilder(url) { Path = DownloadLinkLogRedactor.RedactPath(url.AbsolutePath), Query = string.Empty }.Uri;

        // The name is normally the route template ("GET DownloadLink/Download [token]" - parameter
        // NAMES only), but falls back to the raw path when no route matched.
        if (request.Name?.Contains(url.AbsolutePath, StringComparison.OrdinalIgnoreCase) == true)
            request.Name = request.Name.Replace(url.AbsolutePath, DownloadLinkLogRedactor.RedactPath(url.AbsolutePath), StringComparison.OrdinalIgnoreCase);
    }
}
