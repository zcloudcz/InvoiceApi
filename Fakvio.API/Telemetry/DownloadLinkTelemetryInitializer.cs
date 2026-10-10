using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Http.Extensions;
using System.Text.RegularExpressions;

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

    // The prefix plus everything up to the first character that cannot be part of the path any more
    // (whitespace, query, fragment, quote). Used for free text, where the path sits inside a sentence.
    private static readonly Regex TokenInText = new(
        Regex.Escape(TokenPathPrefix) + @"[^\s?#""']+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="path"/> is a download-link redemption, i.e. contains a token.</summary>
    public static bool HasToken(string? path)
        => path != null && path.StartsWith(TokenPathPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the path with the token replaced; every other path is returned unchanged.</summary>
    public static string RedactPath(string? path) => HasToken(path) ? RedactedPath : path ?? string.Empty;

    /// <summary>
    /// Same as <see cref="RedactPath"/>, but for text that merely CONTAINS the path, e.g. a full URL or the
    /// framework's "Request starting HTTP/1.1 GET https://host/api/download/{token}" log message.
    /// Text without a download path is returned as the same instance (no allocation on the hot path).
    /// </summary>
    public static string? RedactText(string? text)
        => text != null && text.Contains(TokenPathPrefix, StringComparison.OrdinalIgnoreCase)
            ? TokenInText.Replace(text, RedactedPath)
            : text;
}

/// <summary>
/// Takes the download-link token out of everything this process sends to Application Insights (same
/// registration pattern as <see cref="CorrelationIdTelemetryInitializer"/>). The raw path gets there on
/// more than one way:
/// <list type="bullet">
/// <item>request telemetry: <c>Url</c> and, when no MVC action ran, <c>Name</c>;</item>
/// <item>every item: <c>Context.Operation.Name</c>, copied from the request name;</item>
/// <item>traces and exceptions (i.e. everything written through <c>ILogger</c>): ASP.NET Core opens a
/// logging scope per request holding <c>RequestPath</c>, and the Application Insights logger copies scope
/// values and the named values of the log message (<c>Path</c>, <c>FormattedMessage</c>, ...) into
/// <c>customDimensions</c>. The framework's own "Request starting/finished" messages carry the full URL
/// in the message text as well.</item>
/// </list>
/// </summary>
public class DownloadLinkTelemetryInitializer : ITelemetryInitializer
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public DownloadLinkTelemetryInitializer(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>Called for every telemetry item of every type, shortly before it is sent.</summary>
    public void Initialize(ITelemetry telemetry)
    {
        // The operation name is the raw "GET /api/download/{token}" when the request never reached an
        // MVC action (e.g. a 429 from the rate limiter), and it is stamped on every item of the request.
        var operation = telemetry.Context.Operation;
        var operationName = DownloadLinkLogRedactor.RedactText(operation.Name);
        if (!ReferenceEquals(operationName, operation.Name))
            operation.Name = operationName;

        // Every value is checked, not only "RequestPath": which keys exist depends on who logged
        // (scope: RequestPath; hosting and routing messages: Path; any message: FormattedMessage), and
        // a list of key names would silently miss the next one.
        if (telemetry is ISupportProperties { Properties: { } properties })
        {
            // A dictionary must not be changed while it is enumerated, so the (rare) hits are collected
            // first. Nothing is allocated for the usual item that has no download path in it.
            List<KeyValuePair<string, string>>? redacted = null;
            foreach (var property in properties)
            {
                var value = DownloadLinkLogRedactor.RedactText(property.Value);
                if (!ReferenceEquals(value, property.Value))
                    (redacted ??= []).Add(new(property.Key, value!));
            }

            if (redacted != null)
                foreach (var (key, value) in redacted)
                    properties[key] = value;
        }

        switch (telemetry)
        {
            case TraceTelemetry trace:
                trace.Message = DownloadLinkLogRedactor.RedactText(trace.Message);
                break;
            case ExceptionTelemetry exception when exception.Message != null:
                // Message is the formatted log message of LogError(ex, "..."), not the exception's own text.
                exception.Message = DownloadLinkLogRedactor.RedactText(exception.Message);
                break;
            case RequestTelemetry request:
                RedactRequest(request);
                break;
        }
    }

    private void RedactRequest(RequestTelemetry request)
    {
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
