using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Http;

namespace Fakvio.API.Telemetry;

/// <summary>
/// Application Insights telemetry initializer that enriches every telemetry item
/// (request, trace, exception, dependency) with the CorrelationId custom property.
///
/// How it works:
/// 1. CorrelationIdMiddleware stores the CorrelationId in HttpContext.Items["CorrelationId"].
/// 2. This initializer reads that value from HttpContext for every telemetry item.
/// 3. It adds "CorrelationId" to the telemetry's GlobalProperties dictionary.
/// 4. In Application Insights (Azure Portal), you can then:
///    - Search for a specific CorrelationId to see ALL telemetry for that request
///    - Use KQL queries: customDimensions.CorrelationId == "some-guid"
///    - Correlate Blazor UI errors with the exact server-side traces
///
/// Why ITelemetryInitializer?
/// Application Insights has its own operation ID system, but our CorrelationId is a
/// USER-FACING identifier that starts in the browser. By adding it as a custom property,
/// we can trace from browser DevTools (response header) → App Insights → AppLog table.
///
/// Registration:
/// Registered as a singleton in Fakvio.API/Program.cs:
///   services.AddSingleton&lt;ITelemetryInitializer, CorrelationIdTelemetryInitializer&gt;()
/// </summary>
public class CorrelationIdTelemetryInitializer : ITelemetryInitializer
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CorrelationIdTelemetryInitializer(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Called by Application Insights for every telemetry item before it's sent to Azure.
    /// Adds the CorrelationId from HttpContext.Items (set by CorrelationIdMiddleware).
    /// </summary>
    public void Initialize(ITelemetry telemetry)
    {
        // Read the CorrelationId that was stored by CorrelationIdMiddleware.
        // This is null for non-HTTP telemetry (startup events, timer triggers without HttpContext).
        var correlationId = _httpContextAccessor.HttpContext?.Items["CorrelationId"] as string;

        if (!string.IsNullOrEmpty(correlationId))
        {
            // GlobalProperties is the modern replacement for Properties dictionary.
            // These appear as customDimensions in Application Insights KQL queries.
            telemetry.Context.GlobalProperties["CorrelationId"] = correlationId;
        }
    }
}
