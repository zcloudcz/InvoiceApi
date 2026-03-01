using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Fakvio.Infrastructure.Data;

/// <summary>
/// Design-time factory for MasterDbContext — used by EF Core CLI tools (dotnet ef migrations).
///
/// Usage:
///   dotnet ef migrations add Init --context MasterDbContext --output-dir Migrations/Master --project Fakvio.Infrastructure --startup-project Fakvio.API
///   dotnet ef database update --context MasterDbContext --project Fakvio.Infrastructure --startup-project Fakvio.API
///
/// The master context uses the default "public" schema in PostgreSQL.
/// This factory provides a connection string at design time when no DI container is available.
/// At runtime, the connection string comes from appsettings.json via Program.cs DI configuration.
/// </summary>
public class MasterDesignTimeFactory : IDesignTimeDbContextFactory<MasterDbContext>
{
    public MasterDbContext CreateDbContext(string[] args)
    {
        // Try to read from appsettings.json first (preferred — matches runtime config)
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Path.Combine(Directory.GetCurrentDirectory(), "..", "Fakvio.API"))
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        // Fallback connection string for PostgreSQL (used when appsettings not found)
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? "Host=localhost;Database=fakvio;Username=fakvio;Password=YourStrong!Passw0rd";

        var optionsBuilder = new DbContextOptionsBuilder<MasterDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            b => b.MigrationsAssembly("Fakvio.Infrastructure"));

        return new MasterDbContext(optionsBuilder.Options);
    }
}
