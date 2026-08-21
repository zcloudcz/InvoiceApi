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

    // A SECOND Entra-shaped connection string, deliberately different from the one above.
    // The precedence rows below need two distinguishable values, otherwise "the
    // higher-precedence key won" and "some key won" look exactly the same.
    private const string SectionConnectionString =
        "Host=section.postgres.database.azure.com;Database=postgres;Port=5432;" +
        "Username=developer@example.onmicrosoft.com;Ssl Mode=Require;";

    // A key that exists but holds only whitespace — how a single command normally "removes"
    // an environment variable (ConnectionStrings__DefaultConnection="" dotnet ef ...).
    private const string BlankValue = "   ";

    // The two values DatabaseOptions.AuthModeSource reports in this file. Asserting the
    // SOURCE and not only the mode is what makes the rows discriminate: "Password because
    // the fallback forced it" and "Password because the legacy key said so" are different
    // outcomes that a mode-only assertion would happily conflate.
    private const string FallbackAuthModeSource = "Database:AuthMode";
    private const string LegacyAuthModeSource = "UseAzureAdAuthentication (legacy)";

    // The legacy global bool that Fakvio.API/appsettings.json really ships (line 13).
    // DatabaseOptions.Resolve reads it as "this machine wants Azure", and throws when it
    // disagrees with an explicit "Database:AuthMode". Any test that claims the fallback is
    // usable in this repository MUST arrange it — otherwise it certifies a path the real
    // dotnet ef run never takes.
    private const string LegacyAzureKey = "UseAzureAdAuthentication";

    private static IConfigurationBuilder BuilderWith(Dictionary<string, string?> data) =>
        new ConfigurationBuilder().AddInMemoryCollection(data);

    /// <summary>
    /// Mirrors the real design-time layering: Fakvio.API/appsettings.json first, environment
    /// variables on top. The json layer always sets the legacy Azure bool, because that is
    /// what the file really ships; <paramref name="jsonConnectionString"/> adds the shipped
    /// connection string on top of it, for the rows that need the json layer to be beaten
    /// rather than merely empty.
    /// </summary>
    private static IConfigurationBuilder BuilderWithShippedAppSettings(
        Dictionary<string, string?> environmentLayer,
        string? jsonConnectionString = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [LegacyAzureKey] = "true",
                ["ConnectionStrings:DefaultConnection"] = jsonConnectionString
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

    // ---------------------------------------------------------------------
    // Precedence matrix — the keys DatabaseOptions.Resolve ranks against each other
    // ---------------------------------------------------------------------

    [Theory]
    // "Database:ConnectionString" | "ConnectionStrings:DefaultConnection" | resolved connection string | resolved mode | where the mode came from
    [InlineData(null, null, ExpectedLocalFallback, DatabaseAuthMode.Password, FallbackAuthModeSource)]
    [InlineData(null, BlankValue, ExpectedLocalFallback, DatabaseAuthMode.Password, FallbackAuthModeSource)]
    [InlineData(null, AzureEntraIdConnectionString, AzureEntraIdConnectionString, DatabaseAuthMode.AzureEntraId, LegacyAuthModeSource)]
    [InlineData(SectionConnectionString, null, SectionConnectionString, DatabaseAuthMode.AzureEntraId, LegacyAuthModeSource)]
    [InlineData(SectionConnectionString, BlankValue, SectionConnectionString, DatabaseAuthMode.AzureEntraId, LegacyAuthModeSource)]
    [InlineData(SectionConnectionString, AzureEntraIdConnectionString, SectionConnectionString, DatabaseAuthMode.AzureEntraId, LegacyAuthModeSource)]
    public void ApplyLocalFallback_ConnectionStringKeys_StepInOnlyWhenBothAreEmpty(
        string? sectionConnectionString,
        string? defaultConnection,
        string expectedConnectionString,
        DatabaseAuthMode expectedAuthMode,
        string expectedAuthModeSource)
    {
        // The step-aside is the least visible decision in DesignTimeDataSource: the fallback
        // must lose to BOTH connection-string keys, not only to the classic one. Driving all
        // four combinations through the same assertions is what turns "it worked for the case
        // I happened to try" into a decision table.
        //
        // Arranged over the configuration this repository really ships, so every row also
        // answers the question the round-1 bug hid in: what does the fallback do with the
        // legacy Azure bool that appsettings.json sets to true?
        var configuration = DesignTimeDataSource.ApplyLocalFallback(
            BuilderWithShippedAppSettings(new()
            {
                ["Database:ConnectionString"] = sectionConnectionString,
                ["ConnectionStrings:DefaultConnection"] = defaultConnection
            }));

        var options = DatabaseOptions.Resolve(configuration);

        options.ConnectionString.ShouldBe(expectedConnectionString);
        options.AuthMode.ShouldBe(expectedAuthMode);
        options.AuthModeSource.ShouldBe(expectedAuthModeSource);

        // Every row must also survive the fail-fast the application runs at startup: the
        // fallback pairs its password-bearing string with Password mode, and every
        // step-aside row leaves a passwordless Azure string in AzureEntraId mode.
        Should.NotThrow(() => options.Validate());
    }

    [Theory]
    // "Database:AuthMode" | legacy "UseAzureAdAuthentication"
    [InlineData(null, null)]
    [InlineData(null, "true")]
    [InlineData(null, "false")]
    [InlineData("Password", null)]
    [InlineData("Password", "true")]        // conflicting pair — must keep throwing
    [InlineData("Password", "false")]
    [InlineData("AzureEntraId", null)]
    [InlineData("AzureEntraId", "true")]
    [InlineData("AzureEntraId", "false")]   // conflicting pair — must keep throwing
    public void ApplyLocalFallback_ConnectionStringPresent_LeavesEveryAuthKeyUntouched(
        string? authMode,
        string? legacyKey)
    {
        // An auth key leaking past the step-aside guard would silently downgrade a real
        // Entra ID design-time run to password authentication, for which no password exists.
        // Instead of re-listing nine expected outcomes (which would only duplicate
        // DatabaseOptionsTests), assert the property that actually matters: with a connection
        // string present, going through the seam is indistinguishable from not going through
        // it at all — including how it fails.
        var keys = new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = SectionConnectionString,
            ["Database:AuthMode"] = authMode,
            [LegacyAzureKey] = legacyKey
        };

        var withoutSeam = ResolveOutcome.Capture(
            new ConfigurationBuilder().AddInMemoryCollection(keys).Build());

        var throughSeam = ResolveOutcome.Capture(
            DesignTimeDataSource.ApplyLocalFallback(
                new ConfigurationBuilder().AddInMemoryCollection(keys)));

        throughSeam.ShouldBe(withoutSeam);
    }

    [Fact]
    public void ApplyLocalFallback_EnvironmentSuppliesConnectionString_BeatsTheJsonLayer()
    {
        // Adding .AddEnvironmentVariables() on top of the appsettings files is the whole
        // point of this change (a design-time run can now be redirected without editing a
        // file), so pin the direction of that override: the environment layer wins, and the
        // fallback stays out of the way of both.
        var configuration = DesignTimeDataSource.ApplyLocalFallback(
            BuilderWithShippedAppSettings(
                new() { ["ConnectionStrings:DefaultConnection"] = SectionConnectionString },
                jsonConnectionString: AzureEntraIdConnectionString));

        DatabaseOptions.Resolve(configuration).ConnectionString.ShouldBe(SectionConnectionString);
    }

    [Fact]
    public void ApplyLocalFallback_BlankSectionConnectionString_StepsInButResolveStillRefuses()
    {
        // Characterization of an asymmetry that lives in DatabaseOptions.Resolve (#132), not
        // in the fallback: Resolve picks the section key up with "??=", which only fires on
        // null, so a key that EXISTS but is blank shadows the classic key — while the same
        // value counts as "missing" three lines later and produces the not-configured error.
        // The fallback reads blank as missing (consistently with the matrix above) and steps
        // in, yet the configuration it hands back is still rejected, because nothing
        // overrides the blank higher-precedence key.
        //
        // Verified end to end: `Database__ConnectionString= dotnet ef migrations list --context
        // MasterDbContext ...` fails with exactly the message asserted below.
        //
        // Pinned rather than reported as a defect: the outcome is a loud, actionable error
        // and the input is a deliberately blanked-out Database__ConnectionString, so this is
        // not the round-1 situation of a test certifying a broken path as working. If Resolve
        // ever learns to treat blank as missing, this test goes red — delete it and fold the
        // case into the matrix above, where it would then belong.
        var configuration = DesignTimeDataSource.ApplyLocalFallback(
            BuilderWithShippedAppSettings(new()
            {
                ["Database:ConnectionString"] = BlankValue
            }));

        configuration.GetConnectionString("DefaultConnection").ShouldBe(ExpectedLocalFallback);

        Should.Throw<InvalidOperationException>(() => DatabaseOptions.Resolve(configuration))
            .Message.ShouldContain("Database connection string not configured");
    }

    /// <summary>
    /// The observable result of <see cref="DatabaseOptions.Resolve"/> over one configuration:
    /// either the values it resolved, or the message it refused with. Captured as a value so
    /// that two configurations can be compared with a single structural assertion.
    /// </summary>
    private sealed record ResolveOutcome(
        DatabaseAuthMode? AuthMode,
        string? AuthModeSource,
        string? ConnectionString,
        string? Error)
    {
        public static ResolveOutcome Capture(IConfiguration configuration)
        {
            try
            {
                var options = DatabaseOptions.Resolve(configuration);
                return new ResolveOutcome(
                    options.AuthMode, options.AuthModeSource, options.ConnectionString, Error: null);
            }
            catch (InvalidOperationException ex)
            {
                return new ResolveOutcome(
                    AuthMode: null, AuthModeSource: null, ConnectionString: null, ex.Message);
            }
        }
    }
}
