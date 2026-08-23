// ============================================================================
// DiagnosticControllerTests — unit tests for GET /api/diagnostic/health.
//
// The controller is the single source of truth for the health payload (the Azure
// Functions wrapper only delegates to it), so this is where the payload contract is
// pinned:
// - authMode / authModeSource report what the process REALLY resolved, for all three
//   configuration shapes (new key, legacy bool, nothing at all)
// - the payload never carries a password or a raw connection string
// - 200 when the master DB answers, 503 when it does not
// - SysAdmin-only authorization
// ============================================================================

using System.Reflection;
using System.Text.Json;
using Fakvio.API.Controller;
using Fakvio.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

public class DiagnosticControllerTests : IDisposable
{
    // A password-mode connection string with a password worth hunting for in the payload.
    private const string TestConnectionString =
        "Host=localhost;Port=5432;Database=fakvio;Username=fakvio;Password=fakvio_dev";

    private readonly MasterDbContext _masterDb;

    public DiagnosticControllerTests()
    {
        // InMemoryDatabase always answers CanConnectAsync() with true — the "healthy DB" case.
        _masterDb = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase($"DiagnosticControllerTest_{Guid.NewGuid()}")
            .Options);
    }

    public void Dispose()
    {
        _masterDb.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IConfiguration BuildConfiguration(params (string Key, string Value)[] extra)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = TestConnectionString
        };

        foreach (var (key, value) in extra)
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private DiagnosticController CreateSut(IConfiguration configuration, MasterDbContext? masterDb = null) =>
        new(
            masterDb ?? _masterDb,
            // Resolve() is what production uses, so the test exercises the real precedence
            // rules instead of hand-setting AuthMode/AuthModeSource (AuthModeSource has no
            // public setter precisely so it cannot be faked).
            DatabaseOptions.Resolve(configuration),
            Substitute.For<ILogger<DiagnosticController>>());

    private static Dictionary<string, object> Payload(IActionResult result, int expectedStatusCode)
    {
        var objectResult = result.ShouldBeAssignableTo<ObjectResult>()!;
        objectResult.StatusCode.ShouldBe(expectedStatusCode);
        return objectResult.Value.ShouldBeOfType<Dictionary<string, object>>();
    }

    // ── authMode / authModeSource ────────────────────────────────────────────

    /// <summary>
    /// The reason this endpoint exists: after an Azure App Settings change, an operator must
    /// be able to see WHICH key the running process actually obeyed. All three configuration
    /// shapes are covered — the new key, the legacy bool alone, and nothing at all.
    /// </summary>
    [Theory]
    [InlineData("Database:AuthMode", "Password", "Password", "Database:AuthMode")]
    [InlineData("Database:AuthMode", "AzureEntraId", "AzureEntraId", "Database:AuthMode")]
    [InlineData("UseAzureAdAuthentication", "true", "AzureEntraId", "UseAzureAdAuthentication (legacy)")]
    [InlineData("UseAzureAdAuthentication", "false", "Password", "UseAzureAdAuthentication (legacy)")]
    public async Task Health_ReportsTheResolvedAuthModeAndItsSource(
        string key, string value, string expectedMode, string expectedSource)
    {
        // Arrange
        var sut = CreateSut(BuildConfiguration((key, value)));

        // Act
        var data = Payload(await sut.Health(), StatusCodes.Status200OK);

        // Assert
        data["authMode"].ShouldBe(expectedMode);
        data["authModeSource"].ShouldBe(expectedSource);
    }

    /// <summary>
    /// No auth-mode key anywhere = Password, reported as coming from the built-in default.
    /// A self-hosted deployment that never touches the key must be able to see that.
    /// </summary>
    [Fact]
    public async Task Health_WithNoAuthModeConfigured_ReportsTheDefault()
    {
        // Arrange
        var sut = CreateSut(BuildConfiguration());

        // Act
        var data = Payload(await sut.Health(), StatusCodes.Status200OK);

        // Assert
        data["authMode"].ShouldBe("Password");
        data["authModeSource"].ShouldBe("default");
    }

    // ── Security ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole payload is serialized and searched for the password and for the raw
    /// connection string. Asserting on individual fields would miss a secret added to a
    /// NEW field later — this test fails whatever the leak is called.
    /// </summary>
    [Fact]
    public async Task Health_NeverLeaksThePasswordOrTheRawConnectionString()
    {
        // Arrange
        var sut = CreateSut(BuildConfiguration(("Database:AuthMode", "Password")));

        // Act
        var data = Payload(await sut.Health(), StatusCodes.Status200OK);
        var json = JsonSerializer.Serialize(data);

        // Assert
        json.ShouldNotContain("fakvio_dev");
        json.ShouldNotContain("Password=", Case.Insensitive);
        json.ShouldNotContain(TestConnectionString);

        // What IS exposed: host, database and username — the operator-facing identity of
        // the server, rebuilt from a whitelist rather than redacted from a blacklist.
        data["masterConnectionServer"].ShouldBe("Host=localhost; Database=fakvio; Username=fakvio");
    }

    /// <summary>
    /// SysAdmin-only. Health data names the database host and its migration history; that is
    /// operator information, not public information.
    /// </summary>
    [Fact]
    public void Health_IsRestrictedToSysAdmin()
    {
        // Act
        var attribute = typeof(DiagnosticController)
            .GetCustomAttribute<AuthorizeAttribute>();

        // Assert
        attribute.ShouldNotBeNull();
        attribute.Roles.ShouldBe("SysAdmin");
    }

    // ── Connectivity ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reachable database (InMemory) → 200, connected and ready. "Ready" additionally means
    /// no pending migrations; InMemory reports none.
    /// </summary>
    [Fact]
    public async Task Health_WhenTheDatabaseIsReachable_Returns200AndReportsItReady()
    {
        // Arrange
        var sut = CreateSut(BuildConfiguration());

        // Act
        var data = Payload(await sut.Health(), StatusCodes.Status200OK);

        // Assert
        data["masterDbCanConnect"].ShouldBe(true);
        data["databaseConnected"].ShouldBe(true);
        data["databaseReady"].ShouldBe(true);
        data.ShouldContainKey("timestamp");
        data.ShouldContainKey("environment");
    }

    /// <summary>
    /// Unreachable database → 503, so an uptime probe can act on the status code alone.
    /// Port 1 on the loopback interface refuses instantly, which keeps this deterministic
    /// and fast (no DNS, no timeout wait) without needing a real PostgreSQL server.
    /// </summary>
    [Fact]
    public async Task Health_WhenTheDatabaseIsUnreachable_Returns503()
    {
        // Arrange
        using var unreachableDb = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=fakvio;Username=fakvio;Password=fakvio_dev;Timeout=2")
            .Options);
        var sut = CreateSut(BuildConfiguration(), unreachableDb);

        // Act
        var data = Payload(await sut.Health(), StatusCodes.Status503ServiceUnavailable);

        // Assert
        data["databaseConnected"].ShouldBe(false);
        data["databaseReady"].ShouldBe(false);
        // The auth mode is still reported — knowing HOW the process tried to authenticate is
        // exactly what an operator needs while the database is down.
        data.ShouldContainKey("authMode");
        data.ShouldContainKey("authModeSource");
    }

    // ── Payload cleanup (AC of #138) ─────────────────────────────────────────

    /// <summary>
    /// "tenantTemplateConnectionConfigured" reported a connection string no code has used
    /// since the schema-per-tenant migration. Pinned as removed so it does not creep back.
    /// </summary>
    [Fact]
    public async Task Health_DoesNotReportTheDeadTenantTemplateConnectionField()
    {
        // Arrange — the dead key is present in configuration; the payload must still ignore it
        var sut = CreateSut(BuildConfiguration(
            ("ConnectionStrings:TenantTemplateConnection", "Host=localhost;Database=template")));

        // Act
        var data = Payload(await sut.Health(), StatusCodes.Status200OK);

        // Assert
        data.ShouldNotContainKey("tenantTemplateConnectionConfigured");
    }

    /// <summary>
    /// The connection string may come from "Database:ConnectionString" instead of the classic
    /// "ConnectionStrings:DefaultConnection" — and in DatabaseOptions.Resolve the section key
    /// WINS. The payload must therefore describe the string the process actually connected
    /// with, not whichever key the controller happens to look at: reading DefaultConnection
    /// directly used to report "not configured" next to "canConnect: true".
    /// </summary>
    [Fact]
    public async Task Health_WithTheConnectionStringOnlyInTheDatabaseSection_ReportsThatString()
    {
        // Arrange — only the section key exists; ConnectionStrings:DefaultConnection is absent.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] =
                    "Host=section-host;Database=section-db;Username=section-user;Password=fakvio_dev"
            })
            .Build();
        var sut = CreateSut(configuration);

        // Act
        var data = Payload(await sut.Health(), StatusCodes.Status200OK);

        // Assert — configured, and the masked server names the section string's host/db/user.
        data["masterConnectionConfigured"].ShouldBe(true);
        data["masterConnectionServer"]
            .ShouldBe("Host=section-host; Database=section-db; Username=section-user");
        // Same guarantee as the leak test: a different source must not become a new leak path.
        JsonSerializer.Serialize(data).ShouldNotContain("fakvio_dev");
    }

    /// <summary>
    /// Defensive branch: options without a connection string → reported as not configured, no
    /// masked server field invented. DatabaseOptions.Resolve cannot produce this state (it
    /// throws instead), so the options are hand-built here; the guard exists because
    /// ConnectionString is nullable and a null must not crash the health endpoint.
    /// </summary>
    [Fact]
    public async Task Health_WhenTheResolvedOptionsCarryNoConnectionString_ReportsItAsNotConfigured()
    {
        // Arrange
        var sut = new DiagnosticController(
            _masterDb,
            new DatabaseOptions { ConnectionString = null },
            Substitute.For<ILogger<DiagnosticController>>());

        // Act
        var data = Payload(await sut.Health(), StatusCodes.Status200OK);

        // Assert
        data["masterConnectionConfigured"].ShouldBe(false);
        data.ShouldNotContainKey("masterConnectionServer");
    }
}
