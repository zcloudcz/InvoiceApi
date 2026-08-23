using Fakvio.Application.Service;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fakvio.Tests.Integration.Fixtures;

/// <summary>
/// Custom WebApplicationFactory for integration tests.
///
/// Overrides the real API host to:
/// 1. Replace SQL Server with InMemoryDatabase (no real database needed)
/// 2. Replace TenantProvisioningService with a test double (no CREATE DATABASE)
/// 3. Remove background services (LogFlushService, LogCleanupService) that need real SQL
/// 4. Remove DatabaseLoggerProvider (needs LogFlushService to drain its queue)
/// 5. Set environment to "Testing" so the host behaves neither like Development
///    (no Swagger) nor like Production, and so appsettings.Testing.json applies
/// 6. Configure JWT with known test values so we can generate valid tokens
///
/// Note: the startup migration block in Program.cs is skipped because of point 1,
/// not point 5 — it checks Database.IsRelational(), and the InMemory provider is
/// not relational. That is what lets a test override the environment (see
/// SwaggerEnvironmentTests) without MigrateAsync() blowing up on startup.
///
/// After building the host, it calls EnsureCreated() on both DbContexts
/// so that the InMemoryDatabase has the schema (tables) ready and seed data populated.
///
/// Usage: implement IClassFixture&lt;FakvioFactory&gt; in your test class.
/// The factory creates the test server ONCE and shares it across all tests in the class.
/// </summary>
public class FakvioFactory : WebApplicationFactory<Program>
{
    // Unique database names per factory instance — prevents cross-test interference
    // when multiple test classes run in parallel (each gets its own InMemoryDatabase).
    private readonly string _masterDbName = $"MasterDb_{Guid.NewGuid()}";
    private readonly string _tenantDbName = $"TenantDb_{Guid.NewGuid()}";

    /// <summary>
    /// Configures the test web host with InMemoryDatabase, test doubles, and test JWT settings.
    /// This method is called by WebApplicationFactory before the host is built.
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Set environment to "Testing" — picks up appsettings.Testing.json and keeps the host
        // out of Development-only branches (Swagger). It does NOT drive the startup migration
        // block any more: that one checks Database.IsRelational(), so MigrateAsync() is skipped
        // purely because the DbContexts below use the InMemory provider. Tests are therefore
        // free to override the environment via WithWebHostBuilder(b => b.UseEnvironment(...)).
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // ── Replace MasterDbContext with InMemoryDatabase ─────────────────
            // EF Core's AddDbContext registers multiple services internally:
            // - The DbContext itself (scoped)
            // - DbContextOptions<T> (the options container)
            // - IDbContextOptionsConfiguration<T> (internal: holds the UseSqlServer action)
            // ALL of these must be removed to prevent "multiple database providers" error.
            // Using a generic approach to catch all EF Core internal registrations.
            RemoveDbContextRegistrations<MasterDbContext>(services);
            services.AddDbContext<MasterDbContext>(options =>
                options.UseInMemoryDatabase(_masterDbName));

            // ── Replace TenantDbContext with InMemoryDatabase ─────────────────
            // The real TenantDbContext resolves its connection dynamically per-request
            // (from CompanySystemSettings in master DB). In tests, all tenants share
            // the same InMemoryDatabase — tenant isolation is tested via middleware, not DB.
            RemoveDbContextRegistrations<TenantDbContext>(services);
            services.AddDbContext<TenantDbContext>(options =>
                options.UseInMemoryDatabase(_tenantDbName));

            // ── Replace TenantProvisioningService with test double ────────────
            // The real service uses raw SQL (CREATE DATABASE, MigrateAsync) which
            // is incompatible with InMemoryDatabase. Our test double just flips
            // the IsProvisioned/IsActive flags in CompanySystemSettings.
            services.RemoveAll<ITenantProvisioningService>();
            services.AddScoped<ITenantProvisioningService, TestTenantProvisioningService>();

            // ── Remove background services that depend on real SQL Server ─────
            // LogFlushService: drains log queue to AppLog table via raw SqlConnection
            // LogCleanupService: deletes old AppLog entries via raw SqlConnection
            // Both would throw because there's no real SQL Server connection string.
            RemoveHostedService<LogFlushService>(services);
            RemoveHostedService<LogCleanupService>(services);

            // ── Remove DatabaseLoggerProvider ─────────────────────────────────
            // This provider enqueues log entries to a ConcurrentQueue that
            // LogFlushService drains. Without LogFlushService, the queue grows
            // unbounded. Remove it to keep tests clean.
            services.RemoveAll<ILoggerProvider>();
        });

        // ── Configure test JWT settings ──────────────────────────────────────
        // Override appsettings.json JWT values with known test values.
        // These must match what AuthHelper uses to call the login endpoint.
        builder.UseSetting("JwtSettings:Secret", "TestSecretKeyForIntegrationTestsThatMustBeAtLeast32BytesLong!");
        builder.UseSetting("JwtSettings:Issuer", "Fakvio.Tests");
        builder.UseSetting("JwtSettings:Audience", "Fakvio.Tests.Client");

        // ── Configure database auth mode for AddDatabaseContexts ──────────────
        // AddDatabaseContexts now builds its NpgsqlDataSource singleton EAGERLY (inside
        // DatabaseOptions.Validate() + the NpgsqlDataSourceFactory constructor), before any
        // of the ConfigureServices overrides above run. Fakvio.API/appsettings.json points at
        // the Azure host in "AzureEntraId" mode (no password in the connection string), so
        // without this override Validate() would fail host startup for every integration test.
        // A syntactically valid Password-mode connection string is enough — nothing here ever
        // opens a real connection (MasterDbContext/TenantDbContext are swapped to InMemory
        // above; RemoveAll<NpgsqlDataSource>() is NOT used because AzureOperationController,
        // ImapPollService and AdvisoryLock all inject NpgsqlDataSource directly and would fail
        // to construct without it).
        builder.UseSetting("ConnectionStrings:DefaultConnection", "Host=localhost;Port=5432;Database=fakvio;Username=fakvio;Password=fakvio_dev");
        builder.UseSetting("Database:AuthMode", "Password");

        // The legacy flag must also be overridden to agree with the new key — DatabaseOptions.Resolve
        // throws on a genuine mismatch between "Database:AuthMode" and "UseAzureAdAuthentication"
        // (appsettings.json has it hardcoded to true for the Azure host), and there is no
        // appsettings.Testing.json to override it for the "Testing" environment this factory uses.
        builder.UseSetting("UseAzureAdAuthentication", "false");
    }

    /// <summary>
    /// Initializes the InMemoryDatabase with schema and seed data.
    /// Must be called after creating the HttpClient (which builds the host).
    ///
    /// EnsureCreated() creates the schema (tables) from the DbContext model
    /// AND applies HasData() seed entries (SysAdmin user, VAT rates, currencies, etc.).
    /// This is the InMemoryDatabase equivalent of running migrations.
    /// </summary>
    public void InitializeDatabase()
    {
        using var scope = Services.CreateScope();

        // Create master DB schema + seed data (SysAdmin user, currencies, VAT rates, etc.)
        //
        // GOTCHA: EnsureCreated() is a NO-OP when the database already "exists" — and the
        // InMemory store springs into existence as soon as ANY code touches the context
        // during host startup (e.g. Data Protection key persistence writes a key row).
        // In that case the schema exists but HasData seed rows (SysAdmin!) were never
        // inserted, and every login in tests fails with 401. Detect the half-initialized
        // state (no users) and force a delete + recreate, which re-applies the seed.
        var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        masterDb.Database.EnsureCreated();
        if (!masterDb.User.Any())
        {
            masterDb.Database.EnsureDeleted();
            masterDb.Database.EnsureCreated();
        }

        // Create tenant DB schema + seed data (number sequences, currencies, VAT rates, etc.)
        // Same guard as above — Currency is part of the tenant seed, so an empty Currency
        // table means the seed never ran.
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        tenantDb.Database.EnsureCreated();
        if (!tenantDb.Currency.Any())
        {
            tenantDb.Database.EnsureDeleted();
            tenantDb.Database.EnsureCreated();
        }
    }

    /// <summary>
    /// Seeds a regular (non-SysAdmin) user directly into the InMemoryDatabase.
    /// Used by role-authorization tests that need a User-role account to verify
    /// that [Authorize(Roles = "SysAdmin")] returns 403 (not 200) for non-admins.
    ///
    /// The password hash below is BCrypt for "TestUser123" — same algorithm as
    /// the seeded SysAdmin, so login via POST /api/auth/login works normally.
    ///
    /// Call AFTER InitializeDatabase() (schema must exist first).
    /// </summary>
    /// <param name="email">Email to register the user under (must be unique in the test DB)</param>
    /// <param name="userId">Explicit ID to avoid collisions with seed data (seed admin = 1)</param>
    /// <returns>The plain-text password to pass to AuthHelper.LoginAsync()</returns>
    public string SeedRegularUser(string email, long userId = 100)
    {
        const string plainTextPassword = "TestUser123";
        // Use cost factor 4 (minimum) for speed in tests — production uses 12.
        // Cost factor 4 produces a valid BCrypt hash that AuthService.LoginAsync verifies correctly.
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(plainTextPassword, workFactor: 4);

        using var scope = Services.CreateScope();
        var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();

        // Only add if not already present — tests sharing a factory instance call
        // InitializeDatabase() multiple times (it's idempotent), so guard here too.
        if (!masterDb.User.Any(u => u.Email == email))
        {
            masterDb.User.Add(new Fakvio.Domain.Entities.User
            {
                Id = userId,
                Email = email,
                PasswordHash = passwordHash,
                FirstName = "Regular",
                LastName = "User",
                Role = Fakvio.Domain.Enums.EUserRole.User,
                CompanyId = null,
                IsActive = true,
                IsEmailVerified = true,  // pre-verified so login doesn't require email step
                ExternalProvider = Fakvio.Domain.Enums.EExternalProvider.None
            });
            masterDb.SaveChanges();
        }

        return plainTextPassword;
    }

    /// <summary>
    /// Removes ALL service registrations related to a DbContext type.
    ///
    /// EF Core's AddDbContext registers multiple services:
    /// - TContext (the DbContext itself, scoped)
    /// - DbContextOptions&lt;TContext&gt; (the options container)
    /// - IDbContextOptionsConfiguration&lt;TContext&gt; (internal delegate holding UseSqlServer/etc)
    ///
    /// Simply removing DbContextOptions&lt;T&gt; is NOT enough — the internal configuration
    /// delegate still carries the original provider (SqlServer). When AddDbContext is called
    /// again with UseInMemoryDatabase, BOTH providers end up registered, causing:
    /// "Services for database providers 'SqlServer', 'InMemory' have been registered"
    ///
    /// This method removes ALL descriptors whose service type references TContext,
    /// catching all EF Core internal registrations regardless of their exact type name.
    /// </summary>
    private static void RemoveDbContextRegistrations<TContext>(IServiceCollection services)
        where TContext : DbContext
    {
        // Find all descriptors that reference TContext in their service type.
        // This catches: TContext, DbContextOptions<TContext>, IDbContextOptionsConfiguration<TContext>,
        // and any other internal EF Core registrations that reference TContext.
        var contextTypeName = typeof(TContext).FullName!;
        var descriptorsToRemove = services
            .Where(d =>
                d.ServiceType == typeof(TContext) ||
                d.ServiceType == typeof(DbContextOptions<TContext>) ||
                (d.ServiceType.IsGenericType &&
                 d.ServiceType.GenericTypeArguments.Length > 0 &&
                 d.ServiceType.GenericTypeArguments[0] == typeof(TContext)))
            .ToList();

        foreach (var descriptor in descriptorsToRemove)
            services.Remove(descriptor);
    }

    /// <summary>
    /// Helper to remove a hosted service (BackgroundService) from the DI container.
    /// Hosted services are registered as IHostedService, so we find them by implementation type.
    /// </summary>
    private static void RemoveHostedService<TImplementation>(IServiceCollection services)
    {
        var descriptors = services
            .Where(d => d.ServiceType == typeof(IHostedService) &&
                        d.ImplementationType == typeof(TImplementation))
            .ToList();

        foreach (var descriptor in descriptors)
            services.Remove(descriptor);
    }
}
