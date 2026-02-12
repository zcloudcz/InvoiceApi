using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace InvoiceApi.Infrastructure.Data;

/// <summary>
/// Design-time factory for TenantDbContext — used by EF Core CLI tools (dotnet ef migrations).
///
/// Usage:
///   dotnet ef migrations add Init --context TenantDbContext --output-dir Migrations/Tenant --project InvoiceApi.Infrastructure --startup-project InvoiceApi.API
///   dotnet ef database update --context TenantDbContext --project InvoiceApi.Infrastructure --startup-project InvoiceApi.API
///
/// At design time, this uses a "template" tenant database for generating migration files.
/// At runtime, each tenant gets its own database via ITenantDbContextFactory.
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

        // Use a template tenant database for migration generation.
        // This database is used only by EF tools — at runtime, each tenant has its own DB.
        // Fallback connection string for SQL Server (used when appsettings not found)
        var connectionString = configuration.GetConnectionString("TenantTemplateConnection")
            ?? "Server=localhost;Database=invoiceapi_tenant_template;User Id=sa;Password=YourStrong!Passw0rd;TrustServerCertificate=true";

        var optionsBuilder = new DbContextOptionsBuilder<TenantDbContext>();
        optionsBuilder.UseSqlServer(
            connectionString,
            b => b.MigrationsAssembly("InvoiceApi.Infrastructure"));

        return new TenantDbContext(optionsBuilder.Options);
    }
}
