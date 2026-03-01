using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace InvoiceApi.Infrastructure.Data;

/// <summary>
/// Design-time factory for TenantDbContext — used by EF Core CLI tools (dotnet ef migrations).
///
/// Usage:
///   dotnet ef migrations add Init --context TenantDbContext --output-dir Migrations/Tenant --project InvoiceApi.Infrastructure --startup-project InvoiceApi.API
///   dotnet ef database update --context TenantDbContext --project InvoiceApi.Infrastructure --startup-project InvoiceApi.API
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
            .SetBasePath(Path.Combine(Directory.GetCurrentDirectory(), "..", "InvoiceApi.API"))
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        // Use the shared PostgreSQL database connection string.
        // Fallback connection string for PostgreSQL (used when appsettings not found)
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? "Host=localhost;Database=invoiceapi;Username=invoiceapi;Password=YourStrong!Passw0rd";

        var optionsBuilder = new DbContextOptionsBuilder<TenantDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            b => b.MigrationsAssembly("InvoiceApi.Infrastructure"));

        // Register custom model cache key factory for schema-aware model caching
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>();

        // Create context with the template schema — migration SQL will target "tenant_template" schema.
        // When applying migrations to real tenants, TenantProvisioningService creates a context
        // with the actual tenant schema (e.g., "tenant_42") and calls MigrateAsync().
        var context = new TenantDbContext(optionsBuilder.Options);
        context.Schema = "tenant_template";
        return context;
    }
}
