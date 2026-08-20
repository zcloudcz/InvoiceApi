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
///
/// The in-memory sources deliberately mirror what Fakvio.API/appsettings.json really
/// ships (see <c>BuilderWithShippedAppSettings</c>). A bare builder is a friendlier
/// world than the repository actually is, and asserting against it once let a broken
/// <c>dotnet ef</c> path pass as green.
/// </summary>
public class DesignTimeDataSourceTests
{
    // Expected fallback value, spelled out on purpose: the test pins the literal the
    // design-time factories fall back to, instead of re-reading the constant under test.
    private const string ExpectedLocalFallback =
        "Host=localhost;Database=fakvio;Username=fakvio;Password=fakvio_dev";

    // Shape only — the host and the Entra user name are deliberately placeholders, so the
    // test file does not carry the real production endpoint around.
    private const string AzureEntraIdConnectionString =
        "Host=example.postgres.database.azure.com;Database=postgres;Port=5432;" +
        "Username=developer@example.onmicrosoft.com;Ssl Mode=Require;";

    // The legacy global bool that Fakvio.API/appsettings.json really ships (line 13).
    // DatabaseOptions.Resolve reads it as "this machine wants Azure", and throws when it
    // disagrees with an explicit "Database:AuthMode". Any test that claims the fallback is
    // usable in this repository MUST arrange it — otherwise it certifies a path the real
    // dotnet ef run never takes.
    private const string LegacyAzureKey = "UseAzureAdAuthentication";

    private static IConfigurationBuilder BuilderWith(Dictionary<string, string?> data) =>
        new ConfigurationBuilder().AddInMemoryCollection(data);

    /// <summary>
    /// Mirrors the real design-time layering: Fakvio.API/appsettings.json first (which only
    /// ever sets the legacy Azure bool), environment variables on top.
    /// </summary>
    private static IConfigurationBuilder BuilderWithShippedAppSettings(
        Dictionary<string, string?> environmentLayer) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [LegacyAzureKey] = "true"
            })
            .AddInMemoryCollection(environmentLayer);

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
        //
        // Arranged over the configuration this repository actually ships (legacy Azure bool
        // from appsettings.json), because that is where the fallback has to work. An earlier
        // version of this test used a bare in-memory builder, passed green, and the very same
        // command still blew up in a real dotnet ef run.
        var configuration = DesignTimeDataSource.ApplyLocalFallback(
            BuilderWithShippedAppSettings(new()
            {
                ["ConnectionStrings:DefaultConnection"] = "   "
            }));

        configuration.GetConnectionString("DefaultConnection")
            .ShouldBe(ExpectedLocalFallback);
        configuration["Database:AuthMode"].ShouldBe("Password");

        // The assertion that matters: the whole resolve + fail-fast pipeline must survive,
        // not just the individual keys. Reading the keys alone is what missed the bug.
        var options = DatabaseOptions.Resolve(configuration);

        options.AuthMode.ShouldBe(DatabaseAuthMode.Password);
        Should.NotThrow(() => options.Validate());
    }

    [Fact]
    public void ApplyLocalFallback_ShippedAppSettingsSayAzure_FallbackOverridesLegacyKeyToo()
    {
        // Regression guard for the exact command that failed:
        //   ConnectionStrings__DefaultConnection="" dotnet ef migrations list ...
        // Pinning "Database:AuthMode" alone leaves it fighting the legacy bool, and
        // DatabaseOptions.Resolve throws "Conflicting database auth mode configuration".
        var configuration = DesignTimeDataSource.ApplyLocalFallback(
            BuilderWithShippedAppSettings([]));

        configuration[LegacyAzureKey].ShouldBe("false");

        var options = DatabaseOptions.Resolve(configuration);

        options.AuthMode.ShouldBe(DatabaseAuthMode.Password);
        options.ConnectionString.ShouldBe(ExpectedLocalFallback);
        Should.NotThrow(() => options.Validate());
    }

    [Fact]
    public void ApplyLocalFallback_ConnectionStringPresent_LeavesLegacyAzureKeyAlone()
    {
        // The legacy override belongs to the fallback branch only. If it ever leaked out of
        // the "no connection string anywhere" guard, it would silently downgrade a real
        // Azure/Entra design-time run to password auth — which has no password to offer.
        var configuration = DesignTimeDataSource.ApplyLocalFallback(
            BuilderWithShippedAppSettings(new()
            {
                ["ConnectionStrings:DefaultConnection"] = AzureEntraIdConnectionString
            }));

        configuration[LegacyAzureKey].ShouldBe("true");

        var options = DatabaseOptions.Resolve(configuration);

        options.AuthMode.ShouldBe(DatabaseAuthMode.AzureEntraId);
        options.AuthModeSource.ShouldBe("UseAzureAdAuthentication (legacy)");
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
