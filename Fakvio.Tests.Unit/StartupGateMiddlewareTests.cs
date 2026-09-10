using Fakvio.API.Middleware;
using Fakvio.Infrastructure.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for the startup gate on the API host.
///
/// What is actually being protected: Kestrel accepts requests before the Tailscale tunnel
/// has bound its loopback port, so for a moment the connection string points at a port
/// nobody is listening on. Requests that land there used to burn the 15 s connect timeout
/// and end as HTTP 500. The gate turns that into a short 503 + Retry-After.
///
/// StartupState is static (one startup per process), so every test resets it first —
/// otherwise the tests would leak state into each other and into unrelated tests.
/// </summary>
[Collection(nameof(StartupStateCollection))]
public class StartupGateMiddlewareTests : IDisposable
{
    public StartupGateMiddlewareTests() => StartupState.ResetForTests();

    public void Dispose() => StartupState.ResetForTests();

    /// <summary>Middleware whose "next" just records that it ran.</summary>
    private static (StartupGateMiddleware Middleware, Func<bool> NextCalled) CreateMiddleware()
    {
        var nextCalled = false;
        var middleware = new StartupGateMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            Substitute.For<ILogger<StartupGateMiddleware>>());
        return (middleware, () => nextCalled);
    }

    private static HttpContext CreateHttpContext(string path)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;
        httpContext.Response.Body = new MemoryStream();
        return httpContext;
    }

    [Fact]
    public async Task BeforeDatabaseReady_ApiRequest_Gets503WithRetryAfter()
    {
        var httpContext = CreateHttpContext("/api/dashboard");
        var (middleware, nextCalled) = CreateMiddleware();

        await middleware.InvokeAsync(httpContext);

        // The controller must not run — it would open the very connection that is not ready.
        nextCalled().ShouldBeFalse();
        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        // Retry-After is what makes the client's retry legitimate rather than a guess.
        httpContext.Response.Headers.RetryAfter.ToString().ShouldBe("5");
    }

    [Fact]
    public async Task BeforeDatabaseReady_DiagnosticRequest_PassesThrough()
    {
        // Diagnostics is the only endpoint that can explain WHY the database is not ready,
        // so gating it would hide the failure it exists to report.
        var httpContext = CreateHttpContext("/api/diagnostic/health");
        var (middleware, nextCalled) = CreateMiddleware();

        await middleware.InvokeAsync(httpContext);

        nextCalled().ShouldBeTrue();
        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task AfterDatabaseReady_ApiRequest_PassesThrough()
    {
        StartupState.MarkDatabaseReady();
        var httpContext = CreateHttpContext("/api/dashboard");
        var (middleware, nextCalled) = CreateMiddleware();

        await middleware.InvokeAsync(httpContext);

        nextCalled().ShouldBeTrue();
        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }
}

/// <summary>
/// Groups every test class that touches the static <see cref="StartupState"/>. xUnit runs
/// the classes inside one collection sequentially, which is exactly the guarantee needed
/// here — without it one class's MarkDatabaseReady() makes another's "not ready yet"
/// assertion fail at random.
///
/// Deliberately WITHOUT DisableParallelization: this collection only has to be serial with
/// respect to itself, and serializing it against the whole suite just slows the run down.
/// </summary>
[CollectionDefinition(nameof(StartupStateCollection))]
public class StartupStateCollection;
