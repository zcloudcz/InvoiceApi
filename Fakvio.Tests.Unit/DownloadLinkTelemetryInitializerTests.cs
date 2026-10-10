using Fakvio.API.Telemetry;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// The download-link token is a bearer credential inside the URL path. Application Insights stores the
/// URL of every request, so <see cref="DownloadLinkTelemetryInitializer"/> has to take the token out.
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
}
