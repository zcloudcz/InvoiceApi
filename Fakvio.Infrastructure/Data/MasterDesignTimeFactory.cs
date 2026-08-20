using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fakvio.Infrastructure.Data;

/// <summary>
/// Design-time factory for MasterDbContext — used by EF Core CLI tools (dotnet ef migrations).
///
/// Usage:
///   dotnet ef migrations add Init --context MasterDbContext --output-dir Migrations/Master --project Fakvio.Infrastructure --startup-project Fakvio.API
///   dotnet ef database update --context MasterDbContext --project Fakvio.Infrastructure --startup-project Fakvio.API
///
/// The master context uses the default "public" schema in PostgreSQL.
/// This factory provides a connection at design time when no DI container is available.
/// At runtime, the same data source is built by Program.cs DI configuration instead.
///
/// Configuration and the data source itself come from <see cref="DesignTimeDataSource"/>,
/// shared with <see cref="TenantDesignTimeFactory"/>.
/// </summary>
public class MasterDesignTimeFactory : IDesignTimeDbContextFactory<MasterDbContext>
{
    public MasterDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<MasterDbContext>();

        // The data source (not a plain connection string) is what makes `dotnet ef` work
        // against Azure: it carries the Entra ID token provider when that auth mode is on.
        optionsBuilder.UseNpgsql(
            DesignTimeDataSource.Root,
            b => b.MigrationsAssembly("Fakvio.Infrastructure"));

        return new MasterDbContext(optionsBuilder.Options);
    }
}
