// ============================================================================
// DiagnosticFunctionsPipelineTests — SysAdmin gate on the diagnostic endpoints
// verified through the REAL Functions worker pipeline (issue #263).
//
// DiagnosticFunctionsTests hands the function a DefaultHttpContext whose User
// is already populated. That proves the guard, but not the wiring: in the
// Azure Functions Isolated Worker there is no UseAuthentication(), so
// HttpContext.User is only ever set by JwtAuthenticationMiddleware — and that
// middleware runs for every route, with no path allowlist. If it stopped
// validating tokens, the hand-built tests would stay green while production
// handed the migrate/auth payload to anyone.
//
// So these tests drive the actual chain:
//   Authorization header → JwtAuthenticationMiddleware.Invoke(...) → function
// with real HS256 tokens shaped exactly like the ones AuthService issues
// (ClaimTypes.Role, same issuer/audience/secret from configuration).
//
// The only stand-in is FunctionContext: the worker host is what creates it in
// production, so it is substituted here. GetHttpContext() is the genuine
// extension method — it reads FunctionContext.Items["HttpRequestContext"],
// which is exactly what the host fills in.
// ============================================================================

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Fakvio.Functions.HttpFunctions;
using Fakvio.Functions.Middleware;
using Fakvio.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// End-to-end (in-process) tests of the /api/diagnostic/migrate and
/// /api/diagnostic/auth authorization gate, driven by the JWT middleware
/// instead of a hand-built ClaimsPrincipal.
/// </summary>
public class DiagnosticFunctionsPipelineTests : IDisposable
{
    // The signing secret has to be long enough for HMAC-SHA256 (>= 256 bits).
    private const string JwtSecret = "pipeline-test-secret-key-with-at-least-32-chars!!";
    private const string OtherJwtSecret = "an-entirely-different-secret-key-32-chars-long!!";
    private const string JwtIssuer = "Fakvio";
    private const string JwtAudience = "FakvioClient";
    private const string SysAdminRole = "SysAdmin";
    private const string RegularUserRole = "User";

    private readonly ServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly DiagnosticFunctions _sut;
    private readonly JwtAuthenticationMiddleware _jwtMiddleware;

    public DiagnosticFunctionsPipelineTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JwtSettings:Secret"] = JwtSecret,
                ["JwtSettings:Issuer"] = JwtIssuer,
                ["JwtSettings:Audience"] = JwtAudience,
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=test"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddDbContext<MasterDbContext>(options =>
            options.UseInMemoryDatabase($"DiagnosticPipelineTest_{Guid.NewGuid()}"));
        _serviceProvider = services.BuildServiceProvider();

        _sut = new DiagnosticFunctions(
            _serviceProvider,
            _configuration,
            Substitute.For<ILogger<DiagnosticFunctions>>());

        _jwtMiddleware = new JwtAuthenticationMiddleware(
            _configuration,
            Substitute.For<ILogger<JwtAuthenticationMiddleware>>());
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        GC.SuppressFinalize(this);
    }

    // ─── Migrate ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Migrate_WithoutAuthorizationHeader_IsRejectedAsAnonymous()
    {
        var result = await CallThroughPipelineAsync(authorizationHeader: null, InvokeMigrateAsync);

        result.ShouldBeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Migrate_WithRegularUserToken_IsForbidden()
    {
        var header = BearerHeader(CreateToken(RegularUserRole));

        var result = await CallThroughPipelineAsync(header, InvokeMigrateAsync);

        result.ShouldBeOfType<ForbidResult>();
    }

    /// <summary>
    /// A real SysAdmin token must survive the middleware and reach the migration
    /// payload. The InMemory provider cannot migrate, so the function reports the
    /// failure inside a 200 diagnostic body — which is exactly the shape we assert,
    /// proving the caller got the diagnostic data and not an empty OK.
    /// </summary>
    [Fact]
    public async Task Migrate_WithSysAdminToken_ReturnsTheDiagnosticPayload()
    {
        var header = BearerHeader(CreateToken(SysAdminRole));

        var result = await CallThroughPipelineAsync(header, InvokeMigrateAsync);

        var payload = ShouldBeDiagnosticPayload(result);
        payload.ShouldContainKey("masterMigrateSuccess");
        payload.ShouldContainKey("timestamp");
    }

    // ─── Auth diagnostic ────────────────────────────────────────────────────

    [Fact]
    public async Task AuthDiagnostic_WithoutAuthorizationHeader_IsRejectedAsAnonymous()
    {
        var result = await CallThroughPipelineAsync(authorizationHeader: null, InvokeAuthDiagnosticAsync);

        result.ShouldBeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task AuthDiagnostic_WithRegularUserToken_IsForbidden()
    {
        var header = BearerHeader(CreateToken(RegularUserRole));

        var result = await CallThroughPipelineAsync(header, InvokeAuthDiagnosticAsync);

        result.ShouldBeOfType<ForbidResult>();
    }

    /// <summary>
    /// The happy path that only the real pipeline can prove: the middleware must
    /// map the JWT role claim so that IsInRole("SysAdmin") is true inside the
    /// function. If that mapping breaks, every SysAdmin gets a 403 in production.
    /// </summary>
    [Fact]
    public async Task AuthDiagnostic_WithSysAdminToken_ReturnsPayloadWithSysAdminRoleRecognised()
    {
        var header = BearerHeader(CreateToken(SysAdminRole));

        var result = await CallThroughPipelineAsync(header, InvokeAuthDiagnosticAsync);

        var payload = ShouldBeDiagnosticPayload(result);
        payload["isInRole_SysAdmin"].ShouldBe(true);
        payload["userIdentityIsAuthenticated"].ShouldBe(true);
    }

    /// <summary>
    /// Forged token: correct claims, wrong signing key. The middleware swallows the
    /// validation error and leaves the caller anonymous, so the gate must answer 401.
    /// </summary>
    [Fact]
    public async Task AuthDiagnostic_WithTokenSignedByAnotherSecret_IsRejectedAsAnonymous()
    {
        var header = BearerHeader(CreateToken(SysAdminRole, secret: OtherJwtSecret));

        var result = await CallThroughPipelineAsync(header, InvokeAuthDiagnosticAsync);

        result.ShouldBeOfType<UnauthorizedResult>();
    }

    /// <summary>
    /// Expired SysAdmin token, well past the 2-minute clock skew the middleware allows.
    /// </summary>
    [Fact]
    public async Task AuthDiagnostic_WithExpiredSysAdminToken_IsRejectedAsAnonymous()
    {
        var header = BearerHeader(CreateToken(SysAdminRole, expiresIn: TimeSpan.FromMinutes(-30)));

        var result = await CallThroughPipelineAsync(header, InvokeAuthDiagnosticAsync);

        result.ShouldBeOfType<UnauthorizedResult>();
    }

    /// <summary>
    /// Garbage in the Authorization header must not crash the middleware and must
    /// not open the endpoint — the caller stays anonymous.
    /// </summary>
    [Theory]
    [InlineData("Bearer not-a-jwt-at-all")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer ")]
    public async Task AuthDiagnostic_WithUnusableAuthorizationHeader_IsRejectedAsAnonymous(string header)
    {
        var result = await CallThroughPipelineAsync(header, InvokeAuthDiagnosticAsync);

        result.ShouldBeOfType<UnauthorizedResult>();
    }

    // ─── Pipeline plumbing ──────────────────────────────────────────────────

    /// <summary>
    /// Runs one request through JwtAuthenticationMiddleware and then invokes the
    /// endpoint with the very HttpRequest the middleware just processed.
    /// </summary>
    private async Task<IActionResult> CallThroughPipelineAsync(
        string? authorizationHeader,
        Func<HttpRequest, Task<IActionResult>> endpoint)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/api/diagnostic/auth";
        if (authorizationHeader != null)
            httpContext.Request.Headers["Authorization"] = authorizationHeader;

        var functionContext = CreateFunctionContext(httpContext);

        IActionResult? result = null;
        await _jwtMiddleware.Invoke(
            functionContext,
            async _ => result = await endpoint(httpContext.Request));

        return result.ShouldNotBeNull("the middleware must always call the next stage");
    }

    /// <summary>
    /// The worker host builds the FunctionContext in production; here we supply the
    /// one thing the middleware reads from it — the HttpContext behind
    /// GetHttpContext(), which the host stores under Items["HttpRequestContext"].
    /// </summary>
    private static FunctionContext CreateFunctionContext(HttpContext httpContext)
    {
        const string hostHttpContextKey = "HttpRequestContext";

        var functionContext = Substitute.For<FunctionContext>();
        functionContext.Items.Returns(new Dictionary<object, object>
        {
            [hostHttpContextKey] = httpContext
        });
        // The middleware asks the worker scope for IHttpContextAccessor; an empty
        // provider (GetService → null) is the "not registered" branch it tolerates.
        functionContext.InstanceServices.Returns(Substitute.For<IServiceProvider>());

        return functionContext;
    }

    private Task<IActionResult> InvokeMigrateAsync(HttpRequest request) => _sut.Migrate(request);

    private Task<IActionResult> InvokeAuthDiagnosticAsync(HttpRequest request) =>
        Task.FromResult(_sut.AuthDiagnostic(request));

    /// <summary>
    /// Mints a token with the same claim shape AuthService.GenerateJwtTokenAsync uses.
    /// </summary>
    private static string CreateToken(
        string role,
        TimeSpan? expiresIn = null,
        string secret = JwtSecret)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "42"),
            new Claim(ClaimTypes.Email, "sysadmin@example.com"),
            new Claim(ClaimTypes.Role, role)
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: JwtIssuer,
            audience: JwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.Add(expiresIn ?? TimeSpan.FromHours(1)),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string BearerHeader(string token) => $"Bearer {token}";

    private static Dictionary<string, object> ShouldBeDiagnosticPayload(IActionResult result) =>
        result.ShouldBeOfType<OkObjectResult>()
            .Value.ShouldBeOfType<Dictionary<string, object>>();
}
