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
// ============================================================================

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
}
