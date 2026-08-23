// ============================================================================
// DiagnosticFunctionsTests — Unit tests for the health check endpoint.
//
// Verifies that the /api/diagnostic/health endpoint correctly reports:
// - databaseConnected: true/false based on DB connectivity
// - databaseReady: true only when connected AND no pending migrations
// - HTTP 503 when the database is unreachable
// - HTTP 200 when the database is healthy
//
// Uses InMemoryDatabase to simulate a working DB, and NSubstitute
// to simulate a failing DB scenario (CanConnectAsync returns false).
//
// Also covers the SysAdmin gate on /api/diagnostic/migrate and
// /api/diagnostic/auth (401 anonymous, 403 non-SysAdmin, 200 SysAdmin).
// ============================================================================

using System.Security.Claims;
using Fakvio.Functions.HttpFunctions;
using Fakvio.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for DiagnosticFunctions.Health() — the database health check endpoint.
/// Covers the new databaseConnected/databaseReady flags and HTTP status codes.
/// </summary>
public class DiagnosticFunctionsTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DiagnosticFunctions> _logger;

    public DiagnosticFunctionsTests()
    {
        _logger = Substitute.For<ILogger<DiagnosticFunctions>>();

        // Configuration with a dummy connection string (InMemory DB doesn't use it,
        // but the health check reads it to report "configured" status)
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=test",
                ["ConnectionStrings:TenantTemplateConnection"] = "Host=localhost;Database=template"
            })
            .Build();

        // Register a real MasterDbContext backed by InMemoryDatabase.
        // InMemoryDatabase always returns true for CanConnectAsync(),
        // which simulates a healthy DB connection.
        var services = new ServiceCollection();
        services.AddDbContext<MasterDbContext>(options =>
            options.UseInMemoryDatabase($"DiagnosticTest_{Guid.NewGuid()}"));

        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// When the database is reachable (InMemoryDatabase), the health endpoint
    /// should return HTTP 200 with databaseConnected = true.
    /// </summary>
    [Fact]
    public async Task Health_WhenDbIsReachable_ReturnsDatabaseConnectedTrue()
    {
        // Arrange
        var sut = new DiagnosticFunctions(_serviceProvider, _configuration, _logger);
        var httpContext = new DefaultHttpContext();

        // Act
        var actionResult = await sut.Health(httpContext.Request);

        // Assert — should be 200 OK
        var okResult = actionResult.ShouldBeOfType<OkObjectResult>();
        var data = okResult.Value.ShouldBeOfType<Dictionary<string, object>>();

        data["databaseConnected"].ShouldBe(true);
    }

    /// <summary>
    /// When DB is connected and there are no pending migrations,
    /// databaseReady should be true (InMemoryDatabase has no migrations concept,
    /// so GetPendingMigrationsAsync returns empty — which means "ready").
    /// </summary>
    [Fact]
    public async Task Health_WhenDbIsReachableAndNoMigrationsPending_ReturnsDatabaseReadyTrue()
    {
        // Arrange
        var sut = new DiagnosticFunctions(_serviceProvider, _configuration, _logger);
        var httpContext = new DefaultHttpContext();

        // Act
        var actionResult = await sut.Health(httpContext.Request);

        // Assert
        var okResult = actionResult.ShouldBeOfType<OkObjectResult>();
        var data = okResult.Value.ShouldBeOfType<Dictionary<string, object>>();

        data["databaseReady"].ShouldBe(true);
    }

    /// <summary>
    /// When MasterDbContext cannot be resolved (simulating a DB outage),
    /// the health endpoint should return HTTP 503 with databaseConnected = false.
    /// We achieve this by registering a broken service provider that throws on resolve.
    /// </summary>
    [Fact]
    public async Task Health_WhenDbIsUnreachable_Returns503WithDatabaseConnectedFalse()
    {
        // Arrange — register MasterDbContext with an invalid connection string
        // that will fail on CanConnectAsync(). We use a service provider
        // where MasterDbContext resolution throws an exception.
        var brokenServices = new ServiceCollection();
        // Register a factory that throws, simulating DB being completely down
        brokenServices.AddScoped<MasterDbContext>(_ =>
            throw new InvalidOperationException("Simulated DB outage"));

        using var brokenProvider = brokenServices.BuildServiceProvider();
        var sut = new DiagnosticFunctions(brokenProvider, _configuration, _logger);
        var httpContext = new DefaultHttpContext();

        // Act
        var actionResult = await sut.Health(httpContext.Request);

        // Assert — should be 503 Service Unavailable
        var objectResult = actionResult.ShouldBeOfType<ObjectResult>();
        objectResult.StatusCode.ShouldBe(503);

        var data = objectResult.Value.ShouldBeOfType<Dictionary<string, object>>();
        data["databaseConnected"].ShouldBe(false);
        data["databaseReady"].ShouldBe(false);
    }

    /// <summary>
    /// The health endpoint should always include timestamp and environment fields,
    /// regardless of DB connectivity status.
    /// </summary>
    [Fact]
    public async Task Health_Always_IncludesTimestampAndEnvironment()
    {
        // Arrange
        var sut = new DiagnosticFunctions(_serviceProvider, _configuration, _logger);
        var httpContext = new DefaultHttpContext();

        // Act
        var actionResult = await sut.Health(httpContext.Request);

        // Assert
        var okResult = actionResult.ShouldBeOfType<OkObjectResult>();
        var data = okResult.Value.ShouldBeOfType<Dictionary<string, object>>();

        data.ShouldContainKey("timestamp");
        data.ShouldContainKey("environment");
        data["timestamp"].ShouldBeOfType<DateTime>();
    }

    /// <summary>
    /// When connection strings are configured, the health endpoint should report them.
    /// This tests the configuration reading logic, not the actual DB connection.
    /// </summary>
    [Fact]
    public async Task Health_WithConfiguredConnectionStrings_ReportsConfiguredTrue()
    {
        // Arrange
        var sut = new DiagnosticFunctions(_serviceProvider, _configuration, _logger);
        var httpContext = new DefaultHttpContext();

        // Act
        var actionResult = await sut.Health(httpContext.Request);

        // Assert
        var okResult = actionResult.ShouldBeOfType<OkObjectResult>();
        var data = okResult.Value.ShouldBeOfType<Dictionary<string, object>>();

        data["masterConnectionConfigured"].ShouldBe(true);
        data["tenantTemplateConnectionConfigured"].ShouldBe(true);
    }

    /// <summary>
    /// When no connection strings are configured, the endpoint should report false.
    /// </summary>
    [Fact]
    public async Task Health_WithoutConnectionStrings_ReportsConfiguredFalse()
    {
        // Arrange — empty configuration, no connection strings
        var emptyConfig = new ConfigurationBuilder().Build();
        var sut = new DiagnosticFunctions(_serviceProvider, emptyConfig, _logger);
        var httpContext = new DefaultHttpContext();

        // Act
        var actionResult = await sut.Health(httpContext.Request);

        // Assert
        var okResult = actionResult.ShouldBeOfType<OkObjectResult>();
        var data = okResult.Value.ShouldBeOfType<Dictionary<string, object>>();

        data["masterConnectionConfigured"].ShouldBe(false);
        data["tenantTemplateConnectionConfigured"].ShouldBe(false);
    }

    // ─── Authorization gating (issue #263) ──────────────────────────────────
    //
    // /api/diagnostic/migrate writes to the database schema and
    // /api/diagnostic/auth echoes the JWT configuration plus every claim,
    // so both are SysAdmin-only. The trigger stays AuthorizationLevel.Anonymous
    // (that flag only controls the Functions host key) — the JWT role check is
    // inlined in the function, exactly like the generated wrappers do it.

    /// <summary>
    /// Builds an HttpRequest whose HttpContext.User mirrors what
    /// JwtAuthenticationMiddleware would set for the given caller.
    /// </summary>
    private static HttpRequest BuildRequest(bool authenticated, bool sysAdmin = false)
    {
        var ctx = new DefaultHttpContext();

        if (authenticated)
        {
            var claims = new List<Claim> { new("UserId", "42") };
            if (sysAdmin)
                claims.Add(new Claim(ClaimTypes.Role, "SysAdmin"));

            // A non-empty authenticationType is what makes IsAuthenticated true.
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        }

        return ctx.Request;
    }

    [Fact]
    public async Task Migrate_Anonymous_Returns401()
    {
        var sut = new DiagnosticFunctions(_serviceProvider, _configuration, _logger);

        var result = await sut.Migrate(BuildRequest(authenticated: false));

        result.ShouldBeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Migrate_AuthenticatedNonSysAdmin_Returns403()
    {
        var sut = new DiagnosticFunctions(_serviceProvider, _configuration, _logger);

        var result = await sut.Migrate(BuildRequest(authenticated: true, sysAdmin: false));

        result.ShouldBeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Migrate_SysAdmin_PassesTheAuthGate()
    {
        var sut = new DiagnosticFunctions(_serviceProvider, _configuration, _logger);

        var result = await sut.Migrate(BuildRequest(authenticated: true, sysAdmin: true));

        // The InMemory provider cannot actually migrate — the function catches that
        // and still answers 200 with a diagnostic payload. What matters here is that
        // the request got past the auth gate instead of being rejected, and that the
        // caller really receives the migration report (an empty 200 would be a
        // silently broken endpoint).
        var okResult = result.ShouldBeOfType<OkObjectResult>();
        var data = okResult.Value.ShouldBeOfType<Dictionary<string, object>>();

        data.ShouldContainKey("masterMigrateSuccess");
        data.ShouldContainKey("timestamp");
        data["timestamp"].ShouldBeOfType<DateTime>();
    }

    [Fact]
    public void AuthDiagnostic_Anonymous_Returns401AndLeaksNothing()
    {
        var sut = new DiagnosticFunctions(_serviceProvider, _configuration, _logger);

        var result = sut.AuthDiagnostic(BuildRequest(authenticated: false));

        // UnauthorizedResult carries no body at all, so the JWT secret length,
        // issuer/audience and claim dump can no longer reach an anonymous caller.
        result.ShouldBeOfType<UnauthorizedResult>();
    }

    [Fact]
    public void AuthDiagnostic_AuthenticatedNonSysAdmin_Returns403()
    {
        var sut = new DiagnosticFunctions(_serviceProvider, _configuration, _logger);

        var result = sut.AuthDiagnostic(BuildRequest(authenticated: true, sysAdmin: false));

        result.ShouldBeOfType<ForbidResult>();
    }

    [Fact]
    public void AuthDiagnostic_SysAdmin_StillReturnsFullDiagnosticPayload()
    {
        var sut = new DiagnosticFunctions(_serviceProvider, _configuration, _logger);

        var result = sut.AuthDiagnostic(BuildRequest(authenticated: true, sysAdmin: true));

        var okResult = result.ShouldBeOfType<OkObjectResult>();
        var data = okResult.Value.ShouldBeOfType<Dictionary<string, object>>();

        // The endpoint keeps its diagnostic value for the one role allowed to use it.
        data["isInRole_SysAdmin"].ShouldBe(true);
        data.ShouldContainKey("claims");
        data.ShouldContainKey("jwtSecretLength");
    }
}
