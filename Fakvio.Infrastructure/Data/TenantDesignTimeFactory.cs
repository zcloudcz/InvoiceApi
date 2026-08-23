using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Fakvio.Infrastructure.Data;

/// <summary>
/// Design-time factory for TenantDbContext — used by EF Core CLI tools (dotnet ef migrations).
///
/// Usage:
///   dotnet ef migrations add Init --context TenantDbContext --output-dir Migrations/Tenant --project Fakvio.Infrastructure --startup-project Fakvio.API
///   dotnet ef database update --context TenantDbContext --project Fakvio.Infrastructure --startup-project Fakvio.API
///
/// At runtime, each tenant gets its own schema (e.g., "tenant_42") via ITenantDbContextFactory.
///
/// Configuration and the data source itself come from <see cref="DesignTimeDataSource"/>,
/// shared with <see cref="MasterDesignTimeFactory"/>.
/// </summary>
public class TenantDesignTimeFactory : IDesignTimeDbContextFactory<TenantDbContext>
{
    public TenantDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<TenantDbContext>();

        // The data source (not a plain connection string) is what makes `dotnet ef` work
        // against Azure: it carries the Entra ID token provider when that auth mode is on.
        optionsBuilder.UseNpgsql(
            DesignTimeDataSource.Root,
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
