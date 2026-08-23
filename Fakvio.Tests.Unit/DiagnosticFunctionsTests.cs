// ============================================================================
// DiagnosticFunctionsTests — unit tests for the Azure Functions health trigger.
//
// Since #138 the payload itself is built by Fakvio.API DiagnosticController and is
// pinned by DiagnosticControllerTests. What is left to verify HERE is what only the
// Functions host can get wrong:
// - the [Authorize(Roles = "SysAdmin")] check, which must be inlined because the MVC
//   filter pipeline does not run inside a function invocation
// - that a permitted call really reaches the controller (same payload, both hosts)
// ============================================================================

using System.Security.Claims;
using Fakvio.API.Controller;
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

public class DiagnosticFunctionsTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly MasterDbContext _masterDb;
    private readonly ILogger<DiagnosticFunctions> _logger;

    public DiagnosticFunctionsTests()
    {
        _logger = Substitute.For<ILogger<DiagnosticFunctions>>();

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] =
                    "Host=localhost;Port=5432;Database=fakvio;Username=fakvio;Password=fakvio_dev",
                ["Database:AuthMode"] = "Password"
            })
            .Build();

        // InMemoryDatabase always returns true for CanConnectAsync() — a healthy DB.
        _masterDb = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase($"DiagnosticFunctionsTest_{Guid.NewGuid()}")
            .Options);

        // Only Migrate/AuthDiagnostic still resolve out of the service provider; Health goes
        // through the injected controller.
        var services = new ServiceCollection();
        services.AddDbContext<MasterDbContext>(options =>
            options.UseInMemoryDatabase($"DiagnosticFunctionsScope_{Guid.NewGuid()}"));
        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _masterDb.Dispose();
        _serviceProvider.Dispose();
        GC.SuppressFinalize(this);
    }

    private DiagnosticFunctions CreateSut(MasterDbContext? masterDb = null) =>
        new(
            _serviceProvider,
            _configuration,
            new DiagnosticController(
                masterDb ?? _masterDb,
                DatabaseOptions.Resolve(_configuration),
                Substitute.For<ILogger<DiagnosticController>>()),
            _logger);

    private static HttpRequest RequestFrom(ClaimsPrincipal? user = null)
    {
        var httpContext = new DefaultHttpContext();
        if (user != null)
        {
            httpContext.User = user;
        }

        return httpContext.Request;
    }

    private static ClaimsPrincipal UserInRole(string role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], authenticationType: "TestAuth"));

    /// <summary>
    /// No token at all → 401. The function trigger is AuthorizationLevel.Anonymous (that only
    /// disables the Functions host key), so this check is the only thing standing between an
    /// anonymous caller and the database diagnostics.
    /// </summary>
    [Fact]
    public async Task Health_WhenTheCallerIsNotAuthenticated_Returns401()
    {
        // Act
        var result = await CreateSut().Health(RequestFrom());

        // Assert
        result.ShouldBeOfType<UnauthorizedResult>();
    }

    /// <summary>
    /// Authenticated but not a SysAdmin → 403. An ordinary tenant user has no business
    /// knowing the database host or its migration history.
    /// </summary>
    [Fact]
    public async Task Health_WhenTheCallerIsNotSysAdmin_Returns403()
    {
        // Act
        var result = await CreateSut().Health(RequestFrom(UserInRole("Admin")));

        // Assert
        result.ShouldBeOfType<ForbidResult>();
    }

    /// <summary>
    /// SysAdmin → the controller answers, and its payload comes back unchanged. This is the
    /// "both hosts report the same authMode" guarantee in test form.
    /// </summary>
    [Fact]
    public async Task Health_WhenTheCallerIsSysAdmin_ReturnsTheControllerPayload()
    {
        // Act
        var result = await CreateSut().Health(RequestFrom(UserInRole("SysAdmin")));

        // Assert
        var data = result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<Dictionary<string, object>>();

        data["authMode"].ShouldBe("Password");
        data["authModeSource"].ShouldBe("Database:AuthMode");
        data["databaseConnected"].ShouldBe(true);
        data.ShouldNotContainKey("tenantTemplateConnectionConfigured");
    }

    /// <summary>
    /// Unreachable database → the controller answers 503, and the wrapper must hand that
    /// status through untouched: ADMINGUIDE tells monitoring it may watch the status code
    /// alone, in BOTH hosts. The hand-written function this one replaced always returned
    /// OkObjectResult, which would report a dead database as healthy.
    ///
    /// Port 1 on the loopback interface refuses instantly — deterministic, no DNS, no wait.
    /// </summary>
    [Fact]
    public async Task Health_WhenTheDatabaseIsUnreachable_PropagatesThe503FromTheController()
    {
        // Arrange
        using var unreachableDb = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=fakvio;Username=fakvio;Password=fakvio_dev;Timeout=2")
            .Options);

        // Act
        var result = await CreateSut(unreachableDb).Health(RequestFrom(UserInRole("SysAdmin")));

        // Assert — a plain ObjectResult carrying 503, not an OkObjectResult
        var objectResult = result.ShouldBeOfType<ObjectResult>();
        objectResult.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);

        // The diagnostic fields survive the failure path too — that is when they matter most.
        var data = objectResult.Value.ShouldBeOfType<Dictionary<string, object>>();
        data["authMode"].ShouldBe("Password");
        data["authModeSource"].ShouldBe("Database:AuthMode");
        data["databaseConnected"].ShouldBe(false);
    }
}
