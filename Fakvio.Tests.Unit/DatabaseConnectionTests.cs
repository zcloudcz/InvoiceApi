using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests verifying that the application can connect to the PostgreSQL database
/// and that EF Core contexts are properly configured.
///
/// These tests require a running PostgreSQL instance (e.g., via docker compose up).
/// They validate:
/// - MasterDbContext can connect and query the public schema
/// - TenantDbContext can connect (used for tenant schema operations)
/// - Connection string is properly loaded from appsettings
///
/// If these tests fail, check:
/// 1. Docker is running: docker compose ps
/// 2. PostgreSQL is healthy: docker exec fakvio-postgres pg_isready
/// 3. Connection string matches docker-compose.yml credentials
/// </summary>
public class DatabaseConnectionTests : IDisposable
{
    private readonly MasterDbContext _masterContext;
    private readonly TenantDbContext _tenantContext;

    public DatabaseConnectionTests()
    {
        // Load connection string from the API's appsettings.Development.json,
        // same as the real application does at startup.
        var config = new ConfigurationBuilder()
            .SetBasePath(Path.Combine(
                Directory.GetCurrentDirectory(), "..", "..", "..", "..",
                "Fakvio.API"))
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        var connectionString = config.GetConnectionString("DefaultConnection");

        // Build MasterDbContext with real PostgreSQL connection
        var masterOptions = new DbContextOptionsBuilder<MasterDbContext>()
            .UseNpgsql(connectionString, b =>
                b.MigrationsAssembly("Fakvio.Infrastructure"))
            .Options;
        _masterContext = new MasterDbContext(masterOptions);

        // Build TenantDbContext with real PostgreSQL connection
        var tenantOptions = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(connectionString, b =>
                b.MigrationsAssembly("Fakvio.Infrastructure"))
            .Options;
        _tenantContext = new TenantDbContext(tenantOptions);
    }

    public void Dispose()
    {
        _masterContext.Dispose();
        _tenantContext.Dispose();
    }

    [Fact]
    public async Task MasterDbContext_ShouldConnect_ToPostgreSQL()
    {
        // Act — CanConnectAsync() opens a connection and sends a simple query
        var canConnect = await _masterContext.Database.CanConnectAsync();

        // Assert
        canConnect.ShouldBeTrue(
            "Cannot connect to PostgreSQL. Is Docker running? (docker compose up -d)");
    }

    [Fact]
    public async Task TenantDbContext_ShouldConnect_ToPostgreSQL()
    {
        // Act
        var canConnect = await _tenantContext.Database.CanConnectAsync();

        // Assert
        canConnect.ShouldBeTrue(
            "Cannot connect to PostgreSQL. Is Docker running? (docker compose up -d)");
    }

    [Fact]
    public async Task MasterDbContext_ShouldHave_MigrationsApplied()
    {
        // Act — get list of applied migrations from __EFMigrationsHistory table
        var appliedMigrations = await _masterContext.Database
            .GetAppliedMigrationsAsync();

        // Assert — at least the InitPostgres migration should be applied
        appliedMigrations.ShouldNotBeEmpty(
            "No migrations applied to master database. Run: dotnet ef database update --context MasterDbContext");
    }

    [Fact]
    public async Task MasterDbContext_ShouldQuery_UsersTable()
    {
        // Act — simple query against the Users table in public schema.
        // This verifies the schema and table structure is correct.
        var userCount = await _masterContext.User.CountAsync();

        // Assert — count can be 0 (empty DB) or more, but the query must succeed
        userCount.ShouldBeGreaterThanOrEqualTo(0,
            "Failed to query Users table. Schema might be incorrect.");
    }
}
