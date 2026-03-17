using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace Fakvio.Infrastructure.Data;

/// <summary>
/// Design-time factory for TenantDbContext — used by EF Core CLI tools (dotnet ef migrations).
///
/// Usage:
///   dotnet ef migrations add Init --context TenantDbContext --output-dir Migrations/Tenant --project Fakvio.Infrastructure --startup-project Fakvio.API
///   dotnet ef database update --context TenantDbContext --project Fakvio.Infrastructure --startup-project Fakvio.API
///
/// At design time, this uses a "tenant_template" schema for generating migration files.
/// The Schema property is set to "tenant_template" so that HasDefaultSchema() in OnModelCreating
/// generates migration SQL targeting this template schema.
///
/// At runtime, each tenant gets its own schema (e.g., "tenant_42") via ITenantDbContextFactory.
/// </summary>
public class TenantDesignTimeFactory : IDesignTimeDbContextFactory<TenantDbContext>
{
    public TenantDbContext CreateDbContext(string[] args)
    {
        // Try to read from appsettings.json first
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Path.Combine(Directory.GetCurrentDirectory(), "..", "Fakvio.API"))
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        // Use the shared PostgreSQL database connection string.
        // Fallback connection string for PostgreSQL (used when appsettings not found)
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? "Host=localhost;Database=fakvio;Username=fakvio;Password=YourStrong!Passw0rd";

        var optionsBuilder = new DbContextOptionsBuilder<TenantDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            b => b.MigrationsAssembly("Fakvio.Infrastructure"));

        // Register custom model cache key factory for schema-aware model caching
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>();

        // IMPORTANT: Schema is intentionally left as null at design time.
        // This ensures generated migration files do NOT contain hardcoded schema names
        // (e.g., schema: "tenant_template"). Instead, HasDefaultSchema() in OnModelCreating
        // applies the schema DYNAMICALLY at runtime when MigrateAsync() is called.
        //
        // Previously, Schema was set to "tenant_template" here, which caused all migration SQL
        // to hardcode that schema name. This meant MigrateAsync() with Schema="tenant_1" would
        // still create tables in "tenant_template" instead of "tenant_1" — breaking provisioning.
        var context = new TenantDbContext(optionsBuilder.Options);
        return context;
    }
}
