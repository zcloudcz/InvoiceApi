using System.Security.Claims;
using Fakvio.Application.Service;
using Fakvio.Functions.Middleware;
using Fakvio.Infrastructure.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for the Functions-host mirror of API-key authentication.
///
/// This is the risky half of the feature: the isolated worker never calls
/// UseAuthentication(), so nothing in the API host's pipeline protects it. These tests
/// prove the worker middleware reaches the same verdicts as the API host — the shared
/// IApiKeyAuthenticator + ApiKeyRequestGuard are what make that true, and the tests fail
/// the moment the driver stops using them.
/// </summary>
public class ApiKeyFunctionsMiddlewareTests
{
    /// <summary>
    /// Key under which the ASP.NET Core integration stores the HttpContext on the
    /// FunctionContext; this is what FunctionContext.GetHttpContext() reads.
    /// </summary>
    private const string HttpContextItemsKey = "HttpRequestContext";

    private const string ValidKey = "fak_live_ValidTestKeyValidTestKeyValidTestKey12";

    private readonly IApiKeyAuthenticator _authenticator = Substitute.For<IApiKeyAuthenticator>();

    private ApiKeyAuthenticationMiddleware CreateMiddleware()
        => new(Substitute.For<ILogger<ApiKeyAuthenticationMiddleware>>());

    /// <summary>
    /// Builds a FunctionContext carrying a live HttpContext plus a DI scope that can
    /// resolve the authenticator — the two things the middleware pulls off the context.
    /// </summary>
    private FunctionContext CreateFunctionContext(HttpContext httpContext)
    {
        var services = new ServiceCollection()
            .AddSingleton(_authenticator)
            .BuildServiceProvider();

        var context = Substitute.For<FunctionContext>();
        context.Items.Returns(new Dictionary<object, object> { [HttpContextItemsKey] = httpContext });
        context.InstanceServices.Returns(services);

        return context;
    }

    private static HttpContext CreateHttpContext(string? authorization, string method = "GET", string path = "/api/invoice/paged")
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        httpContext.Request.Path = path;
        httpContext.Response.Body = new MemoryStream();

        if (authorization is not null)
            httpContext.Request.Headers.Authorization = authorization;

        return httpContext;
    }

    private static ClaimsPrincipal ApiKeyPrincipal(string scopes, string role = "User")
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "1"),
                new Claim(ClaimTypes.Role, role),
                new Claim(ApiKeyAuthenticationDefaults.ScopeClaimType, scopes)
            ],
            ApiKeyAuthenticationDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role));

    // ─── Authentication ──────────────────────────────────────────────────────

    [Fact]
    public async Task ValidKey_AuthenticatesTheRequest()
    {
        var httpContext = CreateHttpContext($"Bearer {ValidKey}");
        _authenticator.AuthenticateAsync(ValidKey, Arg.Any<CancellationToken>())
            .Returns(ApiKeyPrincipal("read"));
        var nextCalled = false;

        await CreateMiddleware().Invoke(CreateFunctionContext(httpContext), _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        nextCalled.ShouldBeTrue();
        httpContext.User.Identity!.IsAuthenticated.ShouldBeTrue();
        httpContext.User.FindFirst(ClaimTypes.NameIdentifier)!.Value.ShouldBe("1");
    }

    [Fact]
    public async Task RejectedKey_LeavesTheRequestAnonymous()
    {
        var httpContext = CreateHttpContext($"Bearer {ValidKey}");
        // Revoked, expired, unknown, deactivated owner — the authenticator answers null
        // for all of them, and the middleware must not invent an identity from any.
        _authenticator.AuthenticateAsync(ValidKey, Arg.Any<CancellationToken>())
            .Returns((ClaimsPrincipal?)null);
        var nextCalled = false;

        await CreateMiddleware().Invoke(CreateFunctionContext(httpContext), _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        // Anonymous, not blocked: every generated function wrapper starts with an
        // IsAuthenticated check and turns this into the 401, while anonymous endpoints
        // keep working.
        nextCalled.ShouldBeTrue();
        httpContext.User.Identity!.IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public async Task JwtToken_IsLeftToTheJwtMiddleware()
    {
        var httpContext = CreateHttpContext("Bearer eyJhbGciOiJIUzI1NiJ9.e30.signature");

        await CreateMiddleware().Invoke(CreateFunctionContext(httpContext), _ => Task.CompletedTask);

        await _authenticator.DidNotReceiveWithAnyArgs().AuthenticateAsync(default!, default);
        httpContext.User.Identity!.IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public async Task NoAuthorizationHeader_IsLeftAlone()
    {
        var httpContext = CreateHttpContext(authorization: null);

        await CreateMiddleware().Invoke(CreateFunctionContext(httpContext), _ => Task.CompletedTask);

        await _authenticator.DidNotReceiveWithAnyArgs().AuthenticateAsync(default!, default);
    }

    [Fact]
    public async Task TimerTrigger_WithoutHttpContext_PassesThrough()
    {
        var context = Substitute.For<FunctionContext>();
        context.Items.Returns(new Dictionary<object, object>());
        var nextCalled = false;

        await CreateMiddleware().Invoke(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        nextCalled.ShouldBeTrue();
    }

    // ─── Scope enforcement ───────────────────────────────────────────────────

    [Fact]
    public async Task ReadOnlyKey_IsRefusedOnAWrite_WithoutRunningTheFunction()
    {
        var httpContext = CreateHttpContext($"Bearer {ValidKey}", method: "POST", path: "/api/invoice");
        _authenticator.AuthenticateAsync(ValidKey, Arg.Any<CancellationToken>())
            .Returns(ApiKeyPrincipal("read"));
        var nextCalled = false;

        await CreateMiddleware().Invoke(CreateFunctionContext(httpContext), _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        nextCalled.ShouldBeFalse();
        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task ReadWriteKey_MayWrite()
    {
        var httpContext = CreateHttpContext($"Bearer {ValidKey}", method: "POST", path: "/api/invoice");
        _authenticator.AuthenticateAsync(ValidKey, Arg.Any<CancellationToken>())
            .Returns(ApiKeyPrincipal("read,write"));
        var nextCalled = false;

        await CreateMiddleware().Invoke(CreateFunctionContext(httpContext), _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        nextCalled.ShouldBeTrue();
        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task ApiKey_CannotManageApiKeysOnThisHostEither()
    {
        var httpContext = CreateHttpContext($"Bearer {ValidKey}", method: "POST", path: "/api/api-key");
        _authenticator.AuthenticateAsync(ValidKey, Arg.Any<CancellationToken>())
            .Returns(ApiKeyPrincipal("read,write"));

        await CreateMiddleware().Invoke(CreateFunctionContext(httpContext), _ => Task.CompletedTask);

        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task ApiKey_MayCallMeOnThisHost()
    {
        var httpContext = CreateHttpContext($"Bearer {ValidKey}", method: "GET", path: "/api/api-key/me");
        _authenticator.AuthenticateAsync(ValidKey, Arg.Any<CancellationToken>())
            .Returns(ApiKeyPrincipal("read"));
        var nextCalled = false;

        await CreateMiddleware().Invoke(CreateFunctionContext(httpContext), _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        nextCalled.ShouldBeTrue();
        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    // ─── Impersonation hand-off ──────────────────────────────────────────────

    [Fact]
    public async Task SysAdminKey_LeavesCompanyIdForImpersonationMiddleware()
    {
        var httpContext = CreateHttpContext($"Bearer {ValidKey}");
        httpContext.Request.Headers["X-Company-Id"] = "42";
        _authenticator.AuthenticateAsync(ValidKey, Arg.Any<CancellationToken>())
            .Returns(ApiKeyPrincipal("read", role: "SysAdmin"));

        await CreateMiddleware().Invoke(CreateFunctionContext(httpContext), _ => Task.CompletedTask);

        // This middleware must not read X-Company-Id itself — ImpersonationMiddleware runs
        // next and does it for both credentials. Duplicating it here would fork the rule.
        httpContext.User.FindFirst("CompanyId").ShouldBeNull();
        httpContext.User.IsInRole("SysAdmin").ShouldBeTrue();
    }
}
