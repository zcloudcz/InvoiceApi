using Fakvio.Infrastructure.Data;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="DesignTimeDataSource"/> — the configuration rules the EF Core
/// design-time factories (<c>dotnet ef</c>) run on.
///
/// Only the fallback logic is exercised here. It is driven through the internal
/// <c>ApplyLocalFallback</c> seam with an in-memory builder, so no appsettings file and no
/// process environment variable is touched — tests stay isolated and can run in parallel.
/// </summary>
public class DesignTimeDataSourceTests
{
    // Expected fallback value, spelled out on purpose: the test pins the literal the
    // design-time factories fall back to, instead of re-reading the constant under test.
    private const string ExpectedLocalFallback =
        "Host=localhost;Database=fakvio;Username=fakvio;Password=YourStrong!Passw0rd";

    private const string AzureEntraIdConnectionString =
        "Host=zcloudpostgresql.postgres.database.azure.com;Database=postgres;Port=5432;" +
        "Username=zahalos_seznam.cz#EXT#@zahalosseznam.onmicrosoft.com;Ssl Mode=Require;";

    private static IConfigurationBuilder BuilderWith(Dictionary<string, string?> data) =>
        new ConfigurationBuilder().AddInMemoryCollection(data);

    // ---------------------------------------------------------------------
    // Fallback is applied only when nothing else provides a connection string
    // ---------------------------------------------------------------------

    [Fact]
    public void ApplyLocalFallback_NoConnectionStringAnywhere_UsesLocalDockerFallback()
    {
        var configuration = DesignTimeDataSource.ApplyLocalFallback(BuilderWith([]));

        configuration.GetConnectionString("DefaultConnection")
            .ShouldBe(ExpectedLocalFallback);
    }

    [Fact]
    public void ApplyLocalFallback_NoConnectionStringAnywhere_PinsAuthModeToPassword()
    {
        // The fallback connection string carries a password, so the auth mode MUST come
        // along with it — otherwise an "Azure" environment would reject it in Validate().
        var configuration = DesignTimeDataSource.ApplyLocalFallback(BuilderWith([]));

        configuration["Database:AuthMode"].ShouldBe("Password");
    }

    [Fact]
    public void ApplyLocalFallback_NoConnectionStringAnywhere_ProducesOptionsThatValidate()
    {
        // Behavioral guard for the pairing above: the resolved options must survive the
        // same fail-fast validation the application runs at startup.
        var configuration = DesignTimeDataSource.ApplyLocalFallback(BuilderWith([]));

        var options = DatabaseOptions.Resolve(configuration);

        options.AuthMode.ShouldBe(DatabaseAuthMode.Password);
        Should.NotThrow(() => options.Validate());
    }

    [Fact]
    public void ApplyLocalFallback_DefaultConnectionPresent_KeepsItAndDoesNotTouchAuthMode()
    {
        var configuration = DesignTimeDataSource.ApplyLocalFallback(BuilderWith(new()
        {
            ["ConnectionStrings:DefaultConnection"] = AzureEntraIdConnectionString,
            ["Database:AuthMode"] = "AzureEntraId"
        }));

        configuration.GetConnectionString("DefaultConnection").ShouldBe(AzureEntraIdConnectionString);
        configuration["Database:AuthMode"].ShouldBe("AzureEntraId");
    }

    [Fact]
    public void ApplyLocalFallback_SectionConnectionStringPresent_DoesNotForcePasswordMode()
    {
        // "Database:ConnectionString" is the higher-precedence key in DatabaseOptions.Resolve.
        // Ignoring it here would drop the localhost fallback (harmless) AND force Password
        // mode (not harmless) on top of a valid Entra ID configuration.
        var configuration = DesignTimeDataSource.ApplyLocalFallback(BuilderWith(new()
        {
            ["Database:ConnectionString"] = AzureEntraIdConnectionString,
            ["Database:AuthMode"] = "AzureEntraId"
        }));

        configuration["Database:AuthMode"].ShouldBe("AzureEntraId");

        var options = DatabaseOptions.Resolve(configuration);
        options.AuthMode.ShouldBe(DatabaseAuthMode.AzureEntraId);
        options.ConnectionString.ShouldBe(AzureEntraIdConnectionString);
    }

    [Fact]
    public void ApplyLocalFallback_BlankConnectionString_IsTreatedAsMissing()
    {
        // An empty ConnectionStrings__DefaultConnection environment variable is a common way
        // to "unset" the value; it must behave like no value at all, not like a broken one.
        var configuration = DesignTimeDataSource.ApplyLocalFallback(BuilderWith(new()
        {
            ["ConnectionStrings:DefaultConnection"] = "   "
        }));

        configuration.GetConnectionString("DefaultConnection")
            .ShouldBe(ExpectedLocalFallback);
        configuration["Database:AuthMode"].ShouldBe("Password");
    }

    // ---------------------------------------------------------------------
    // Source precedence — the fallback must never beat a real source
    // ---------------------------------------------------------------------

    [Fact]
    public void ApplyLocalFallback_LaterSourceSuppliesConnectionString_FallbackNotApplied()
    {
        // Mirrors production layering: appsettings first, environment variables on top.
        // The value from the later source wins and no fallback is added.
        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = AzureEntraIdConnectionString
            });

        var configuration = DesignTimeDataSource.ApplyLocalFallback(builder);

        configuration.GetConnectionString("DefaultConnection").ShouldBe(AzureEntraIdConnectionString);
        configuration["Database:AuthMode"].ShouldBeNull();
    }
}
