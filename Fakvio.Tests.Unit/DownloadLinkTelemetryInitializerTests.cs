using Fakvio.API.Telemetry;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// The download-link token is a bearer credential inside the URL path. Application Insights stores the
/// URL of every request and, through the logging scope, the path on every trace and exception, so
/// <see cref="DownloadLinkTelemetryInitializer"/> has to take the token out of all of them.
/// </summary>
public class DownloadLinkTelemetryInitializerTests
{
    private const string Token = "CfDJ8-secret-download-token";

    private static DownloadLinkTelemetryInitializer Create(HttpContext? httpContext = null)
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);
        return new DownloadLinkTelemetryInitializer(accessor);
    }

    [Fact]
    public void Initialize_DownloadRequest_RedactsTokenInUrlAndName()
    {
        var request = new RequestTelemetry
        {
            Url = new Uri($"https://api.example.com/api/download/{Token}?x=1"),
            Name = $"GET /api/download/{Token}"
        };

        Create().Initialize(request);

        request.Url.ToString().ShouldBe("https://api.example.com/api/download/[redacted]");
        request.Name.ShouldBe("GET /api/download/[redacted]");
    }

    [Fact]
    public void Initialize_UrlNotSetYet_TakesItFromTheLiveRequest()
    {
        // The SDK calls initializers when the request starts, before it fills in Url.
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("api.example.com");
        http.Request.Path = $"/api/download/{Token}";
        var request = new RequestTelemetry();

        Create(http).Initialize(request);

        request.Url.ShouldNotBeNull();
        request.Url.ToString().ShouldNotContain(Token);
    }

    [Fact]
    public void Initialize_OtherRequests_AreLeftAlone()
    {
        // /api/download-links (minting) carries no token in its path and must stay readable.
        var request = new RequestTelemetry { Url = new Uri("https://api.example.com/api/download-links"), Name = "POST DownloadLink/CreateLink" };

        Create().Initialize(request);

        request.Url.ToString().ShouldBe("https://api.example.com/api/download-links");
        request.Name.ShouldBe("POST DownloadLink/CreateLink");
    }

    // ── Traces and exceptions (everything written through ILogger) ──────────────────────────────
    // ASP.NET Core opens a logging scope per request with the raw RequestPath, and the Application
    // Insights logger copies scope values and message values into the item's properties
    // (customDimensions). The property names below are the ones a real request produces.

    [Fact]
    public void Initialize_Trace_RedactsTokenInScopeAndMessageProperties()
    {
        var trace = new TraceTelemetry($"Request starting HTTP/1.1 GET https://api.example.com/api/download/{Token}?x=1 - - -");
        trace.Properties["RequestPath"] = $"/api/download/{Token}";           // hosting scope
        trace.Properties["Path"] = $"/api/download/{Token}";                  // hosting / routing message value
        trace.Properties["FormattedMessage"] = $"No candidates found for the request path '/api/download/{Token}'";
        trace.Properties["RequestId"] = "0HN123:00000001";

        Create().Initialize(trace);

        trace.Properties["RequestPath"].ShouldBe("/api/download/[redacted]");
        trace.Properties["Path"].ShouldBe("/api/download/[redacted]");
        trace.Properties["FormattedMessage"].ShouldBe("No candidates found for the request path '/api/download/[redacted]'");
        trace.Properties["RequestId"].ShouldBe("0HN123:00000001");
        trace.Message.ShouldBe("Request starting HTTP/1.1 GET https://api.example.com/api/download/[redacted]?x=1 - - -");
    }

    [Fact]
    public void Initialize_Exception_RedactsTokenInScopeProperties()
    {
        // The case that matters most: an export throws while the link is still redeemable.
        var exception = new ExceptionTelemetry(new InvalidOperationException("PDF render failed"))
        {
            Message = $"Unhandled exception GET /api/download/{Token}"
        };
        exception.Properties["RequestPath"] = $"/api/download/{Token}";

        Create().Initialize(exception);

        exception.Properties["RequestPath"].ShouldBe("/api/download/[redacted]");
        exception.Message.ShouldBe("Unhandled exception GET /api/download/[redacted]");
    }

    [Fact]
    public void Initialize_AnyTelemetry_RedactsTokenInOperationName()
    {
        // No MVC action ran (e.g. 429 from the rate limiter) -> the operation name is the raw path.
        var trace = new TraceTelemetry("Rate limit exceeded");
        trace.Context.Operation.Name = $"GET /api/download/{Token}";

        Create().Initialize(trace);

        trace.Context.Operation.Name.ShouldBe("GET /api/download/[redacted]");
    }

    [Fact]
    public void Initialize_TraceOfAnotherRequest_IsLeftAlone()
    {
        var trace = new TraceTelemetry("Download link created: kind=InvoicePdf");
        trace.Properties["RequestPath"] = "/api/download-links";
        trace.Context.Operation.Name = "POST DownloadLink/CreateLink";

        Create().Initialize(trace);

        trace.Properties["RequestPath"].ShouldBe("/api/download-links");
        trace.Message.ShouldBe("Download link created: kind=InvoicePdf");
        trace.Context.Operation.Name.ShouldBe("POST DownloadLink/CreateLink");
    }
}
